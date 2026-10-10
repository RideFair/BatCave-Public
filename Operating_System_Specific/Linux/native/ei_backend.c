/* Narrow stable Python ABI around libei 1.2+. No evdev, uinput or root access. */
#include <libei.h>
#include <fcntl.h>
#include <math.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <time.h>

#define LIMIT 64
struct slot { struct ei_device *device; bool resumed; };
struct bc_context { struct ei *ei; struct slot devices[LIMIT]; bool active, failed; uint32_t sequence; };
struct bc_region { double x, y, width, height; };

static uint64_t now_us(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000000 + (uint64_t)ts.tv_nsec / 1000;
}

static struct ei_device *find(struct bc_context *ctx, enum ei_device_capability cap) {
    for (int i = 0; i < LIMIT; i++)
        if (ctx->devices[i].device && ctx->devices[i].resumed && ei_device_has_capability(ctx->devices[i].device, cap))
            return ctx->devices[i].device;
    return NULL;
}

void bc_close(struct bc_context *ctx) {
    if (!ctx) return;
    for (int i = 0; i < LIMIT; i++) {
        if (ctx->devices[i].device) {
            if (ctx->active && ctx->devices[i].resumed) ei_device_stop_emulating(ctx->devices[i].device);
            ei_device_unref(ctx->devices[i].device);
        }
    }
    if (ctx->ei) ei_unref(ctx->ei);
    free(ctx);
}

struct bc_context *bc_open(int fd) {
    struct bc_context *ctx = calloc(1, sizeof(*ctx));
    if (!ctx) return NULL;
    ctx->ei = ei_new_sender(NULL);
    if (!ctx->ei) { free(ctx); return NULL; }
    ei_configure_name(ctx->ei, "BatCave");
    int owned_fd = fcntl(fd, F_DUPFD_CLOEXEC, 3);
    if (owned_fd < 0 || ei_setup_backend_fd(ctx->ei, owned_fd) != 0) { bc_close(ctx); return NULL; }
    return ctx;
}

int bc_fd(struct bc_context *ctx) { return ei_get_fd(ctx->ei); }

int bc_poll(struct bc_context *ctx) {
    if (ctx->failed) return -1;
    ei_dispatch(ctx->ei);
    struct ei_event *event;
    while ((event = ei_get_event(ctx->ei))) {
        enum ei_event_type type = ei_event_get_type(event);
        struct ei_device *device = ei_event_get_device(event);
        switch (type) {
        case EI_EVENT_DISCONNECT: ctx->failed = true; break;
        case EI_EVENT_SEAT_ADDED:
            ei_seat_bind_capabilities(ei_event_get_seat(event), EI_DEVICE_CAP_POINTER_ABSOLUTE,
                                      EI_DEVICE_CAP_KEYBOARD, EI_DEVICE_CAP_BUTTON, EI_DEVICE_CAP_SCROLL, NULL);
            break;
        case EI_EVENT_DEVICE_ADDED: {
            int slot = 0;
            while (slot < LIMIT && ctx->devices[slot].device) slot++;
            if (slot == LIMIT) { ctx->failed = true; break; }
            ctx->devices[slot].device = ei_device_ref(device);
            break;
        }
        case EI_EVENT_DEVICE_RESUMED:
        case EI_EVENT_DEVICE_PAUSED:
        case EI_EVENT_DEVICE_REMOVED:
            for (int i = 0; i < LIMIT; i++) if (ctx->devices[i].device == device) {
                if (ctx->active && type != EI_EVENT_DEVICE_RESUMED) ctx->failed = true;
                ctx->devices[i].resumed = type == EI_EVENT_DEVICE_RESUMED;
                if (type == EI_EVENT_DEVICE_REMOVED) {
                    ei_device_unref(device);
                    ctx->devices[i].device = NULL;
                }
            }
            break;
        default: break;
        }
        ei_event_unref(event);
    }
    return ctx->failed ? -1 : 0;
}

int bc_regions(struct bc_context *ctx, struct bc_region *result, int capacity) {
    int count = 0;
    for (int i = 0; i < LIMIT; i++) {
        struct ei_device *device = ctx->devices[i].device;
        if (!device || !ctx->devices[i].resumed || !ei_device_has_capability(device, EI_DEVICE_CAP_POINTER_ABSOLUTE)) continue;
        struct ei_region *region;
        for (size_t j = 0; (region = ei_device_get_region(device, j)); j++) {
            if (count == capacity) return -1;
            result[count++] = (struct bc_region){ei_region_get_x(region), ei_region_get_y(region),
                                                ei_region_get_width(region), ei_region_get_height(region)};
        }
    }
    return count;
}

int bc_ready(struct bc_context *ctx) {
    struct bc_region regions[128];
    return !ctx->failed && find(ctx, EI_DEVICE_CAP_POINTER_ABSOLUTE) && find(ctx, EI_DEVICE_CAP_KEYBOARD)
        && find(ctx, EI_DEVICE_CAP_BUTTON) && find(ctx, EI_DEVICE_CAP_SCROLL) && bc_regions(ctx, regions, 128) > 0;
}

int bc_start(struct bc_context *ctx) {
    if (ctx->active || !bc_ready(ctx)) return -1;
    ctx->sequence++;
    for (int i = 0; i < LIMIT; i++) if (ctx->devices[i].device && ctx->devices[i].resumed)
        ei_device_start_emulating(ctx->devices[i].device, ctx->sequence);
    ctx->active = true;
    return 0;
}

void bc_stop(struct bc_context *ctx) {
    if (ctx->active) for (int i = 0; i < LIMIT; i++) if (ctx->devices[i].device && ctx->devices[i].resumed)
        ei_device_stop_emulating(ctx->devices[i].device);
    ctx->active = false;
}

int bc_move(struct bc_context *ctx, double *x, double *y) {
    if (!ctx->active || ctx->failed || !isfinite(*x) || !isfinite(*y)) return -1;
    double distance = INFINITY, best_x = 0, best_y = 0;
    struct ei_device *best = NULL;
    for (int i = 0; i < LIMIT; i++) {
        struct ei_device *device = ctx->devices[i].device;
        if (!device || !ctx->devices[i].resumed || !ei_device_has_capability(device, EI_DEVICE_CAP_POINTER_ABSOLUTE)) continue;
        struct ei_region *region;
        for (size_t j = 0; (region = ei_device_get_region(device, j)); j++) {
            double rx = ei_region_get_x(region), ry = ei_region_get_y(region);
            double w = ei_region_get_width(region), h = ei_region_get_height(region);
            if (w < 1 || h < 1) continue;
            double px = fmin(fmax(*x, rx), rx + w - 1), py = fmin(fmax(*y, ry), ry + h - 1);
            double d = (px - *x) * (px - *x) + (py - *y) * (py - *y);
            if (d < distance) { distance = d; best_x = px; best_y = py; best = device; }
        }
    }
    if (!best) return -1;
    ei_device_pointer_motion_absolute(best, best_x, best_y);
    ei_device_frame(best, now_us());
    *x = best_x; *y = best_y;
    return 0;
}

int bc_key(struct bc_context *ctx, uint32_t key, int down) {
    struct ei_device *device = find(ctx, EI_DEVICE_CAP_KEYBOARD);
    if (!ctx->active || ctx->failed || !device || key > 767) return -1;
    ei_device_keyboard_key(device, key, down != 0);
    ei_device_frame(device, now_us());
    return 0;
}

int bc_button(struct bc_context *ctx, uint32_t button, int down) {
    struct ei_device *device = find(ctx, EI_DEVICE_CAP_BUTTON);
    if (!ctx->active || ctx->failed || !device || button < 272 || button > 276) return -1;
    ei_device_button_button(device, button, down != 0);
    ei_device_frame(device, now_us());
    return 0;
}

int bc_scroll(struct bc_context *ctx, int32_t x, int32_t y) {
    struct ei_device *device = find(ctx, EI_DEVICE_CAP_SCROLL);
    if (!ctx->active || ctx->failed || !device) return -1;
    ei_device_scroll_discrete(device, x, y);
    ei_device_frame(device, now_us());
    return 0;
}
