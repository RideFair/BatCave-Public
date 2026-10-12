/* Passive InputCapture transport. Only the compositor can start input delivery. */
#include <libei.h>
#include <fcntl.h>
#include <math.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <unistd.h>

#define DEVICES 64
#define EVENTS 512
struct capture_event { uint32_t kind, sequence, code, down; double x, y; };
struct capture_slot { struct ei_device *device; bool active; uint32_t sequence; };
struct capture_context { struct ei *ei; struct capture_slot slots[DEVICES];
                         struct capture_event events[EVENTS]; unsigned head, count; bool failed; };

static void push(struct capture_context *c, uint32_t kind, uint32_t sequence, uint32_t code, uint32_t down, double x, double y) {
    if (c->count == EVENTS || !isfinite(x) || !isfinite(y) || fabs(x) > 100000 || fabs(y) > 100000) {
        c->failed = true; return;
    }
    c->events[(c->head + c->count++) % EVENTS] = (struct capture_event){kind, sequence, code, down, x, y};
}

void bc_capture_close(struct capture_context *c) {
    if (!c) return;
    for (int i = 0; i < DEVICES; i++) if (c->slots[i].device) ei_device_unref(c->slots[i].device);
    if (c->ei) ei_unref(c->ei);
    free(c);
}

struct capture_context *bc_capture_open(int fd) {
    struct capture_context *c = calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->ei = ei_new_receiver(NULL);
    if (!c->ei) { free(c); return NULL; }
    ei_configure_name(c->ei, "BatCave input capture");
    int owned = fcntl(fd, F_DUPFD_CLOEXEC, 3);
    if (owned < 0) { bc_capture_close(c); return NULL; }
    if (ei_setup_backend_fd(c->ei, owned) != 0) { close(owned); bc_capture_close(c); return NULL; }
    return c;
}

int bc_capture_poll(struct capture_context *c) {
    if (c->failed) return -1;
    ei_dispatch(c->ei);
    struct ei_event *event;
    while ((event = ei_get_event(c->ei))) {
        enum ei_event_type type = ei_event_get_type(event);
        struct ei_device *device = ei_event_get_device(event);
        struct capture_slot *slot = NULL;
        for (int i = 0; i < DEVICES; i++) if (c->slots[i].device == device && device) slot = &c->slots[i];
        switch (type) {
        case EI_EVENT_DISCONNECT: c->failed = true; break;
        case EI_EVENT_SEAT_ADDED:
            ei_seat_bind_capabilities(ei_event_get_seat(event), EI_DEVICE_CAP_POINTER, EI_DEVICE_CAP_KEYBOARD,
                                     EI_DEVICE_CAP_BUTTON, EI_DEVICE_CAP_SCROLL, NULL);
            break;
        case EI_EVENT_DEVICE_ADDED: {
            int i = 0; while (i < DEVICES && c->slots[i].device) i++;
            if (i == DEVICES) c->failed = true;
            else c->slots[i].device = ei_device_ref(device);
            break;
        }
        case EI_EVENT_DEVICE_REMOVED:
            if (slot) { ei_device_unref(slot->device); slot->device = NULL; slot->active = false; c->failed = true; }
            break;
        case EI_EVENT_DEVICE_PAUSED:
            if (slot && slot->active) c->failed = true;
            break;
        case EI_EVENT_DEVICE_START_EMULATING:
            if (!slot) { c->failed = true; break; }
            slot->sequence = ei_event_emulating_get_sequence(event); slot->active = true;
            push(c, 1, slot->sequence, 0, 0, 0, 0); break;
        case EI_EVENT_DEVICE_STOP_EMULATING:
            if (slot) { push(c, 2, slot->sequence, 0, 0, 0, 0); slot->active = false; }
            break;
        case EI_EVENT_POINTER_MOTION:
            if (slot && slot->active) push(c, 3, slot->sequence, 0, 0, ei_event_pointer_get_dx(event), ei_event_pointer_get_dy(event));
            break;
        case EI_EVENT_BUTTON_BUTTON:
            if (slot && slot->active) push(c, 4, slot->sequence, ei_event_button_get_button(event), ei_event_button_get_is_press(event), 0, 0);
            break;
        case EI_EVENT_KEYBOARD_KEY:
            if (slot && slot->active) push(c, 5, slot->sequence, ei_event_keyboard_get_key(event), ei_event_keyboard_get_key_is_press(event), 0, 0);
            break;
        case EI_EVENT_SCROLL_DISCRETE:
            if (slot && slot->active) push(c, 6, slot->sequence, (uint32_t)(slot - c->slots), 0, ei_event_scroll_get_discrete_dx(event), ei_event_scroll_get_discrete_dy(event));
            break;
        case EI_EVENT_SCROLL_DELTA:
            if (slot && slot->active) push(c, 7, slot->sequence, (uint32_t)(slot - c->slots), 0, ei_event_scroll_get_dx(event), ei_event_scroll_get_dy(event));
            break;
        case EI_EVENT_FRAME:
            if (slot && slot->active) push(c, 8, slot->sequence, (uint32_t)(slot - c->slots), 0, 0, 0);
            break;
        case EI_EVENT_KEYBOARD_MODIFIERS:
            if (slot && slot->active) {
                /* Modifier masks are uint32, not pointer deltas. */
                if (c->count == EVENTS) c->failed = true;
                else c->events[(c->head + c->count++) % EVENTS] = (struct capture_event){
                    9, slot->sequence, (uint32_t)(slot - c->slots), ei_event_keyboard_get_xkb_mods_locked(event),
                    ei_event_keyboard_get_xkb_mods_depressed(event), ei_event_keyboard_get_xkb_mods_latched(event)};
            }
            break;
        default: break;
        }
        ei_event_unref(event);
    }
    return c->failed ? -1 : 0;
}

int bc_capture_ready(struct capture_context *c) {
    bool pointer = false, keyboard = false, button = false, scroll = false;
    if (c->failed) return 0;
    for (int i = 0; i < DEVICES; i++) if (c->slots[i].device) {
        struct ei_device *d = c->slots[i].device;
        pointer |= ei_device_has_capability(d, EI_DEVICE_CAP_POINTER);
        keyboard |= ei_device_has_capability(d, EI_DEVICE_CAP_KEYBOARD);
        button |= ei_device_has_capability(d, EI_DEVICE_CAP_BUTTON);
        scroll |= ei_device_has_capability(d, EI_DEVICE_CAP_SCROLL);
    }
    return pointer && keyboard && button && scroll;
}

int bc_capture_next(struct capture_context *c, struct capture_event *event) {
    if (c->failed) return -1;
    if (!c->count) return 0;
    *event = c->events[c->head]; c->head = (c->head + 1) % EVENTS; c->count--;
    return 1;
}

int bc_capture_keymap(struct capture_context *c, uint32_t index, char *buffer, uint32_t capacity) {
    if (c->failed || index >= DEVICES || !c->slots[index].device) return -1;
    struct ei_keymap *map = ei_device_keyboard_get_keymap(c->slots[index].device);
    if (!map || ei_keymap_get_type(map) != EI_KEYMAP_TYPE_XKB) return -1;
    size_t size = ei_keymap_get_size(map);
    if (!size || size > 262144) return -1;
    if (!buffer) return (int)size;
    if (capacity < size) return -1;
    size_t done = 0;
    while (done < size) {
        ssize_t count = pread(ei_keymap_get_fd(map), buffer + done, size - done, (off_t)done);
        if (count <= 0) return -1;
        done += (size_t)count;
    }
    return (int)size;
}
