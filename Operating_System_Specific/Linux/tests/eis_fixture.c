/* Test-only in-process EIS service. It cannot inject input into a real desktop. */
#include <libeis.h>
#include <stdint.h>
#include <stdlib.h>

struct fixture { struct eis *eis; struct eis_seat *seat; struct eis_device *device;
                 int events, keys_down, buttons_down; double x, y; };

struct fixture *fixture_open(int *fd) {
    struct fixture *f = calloc(1, sizeof(*f));
    if (!f) return NULL;
    f->eis = eis_new(NULL);
    if (!f->eis || eis_setup_backend_fd(f->eis) != 0) { free(f); return NULL; }
    *fd = eis_backend_fd_add_client(f->eis);
    return f;
}

void fixture_poll(struct fixture *f) {
    eis_dispatch(f->eis);
    struct eis_event *event;
    while ((event = eis_get_event(f->eis))) {
        switch (eis_event_get_type(event)) {
        case EIS_EVENT_CLIENT_CONNECT: {
            struct eis_client *client = eis_event_get_client(event);
            eis_client_connect(client);
            f->seat = eis_client_new_seat(client, "test-seat");
            eis_seat_configure_capability(f->seat, EIS_DEVICE_CAP_POINTER_ABSOLUTE);
            eis_seat_configure_capability(f->seat, EIS_DEVICE_CAP_KEYBOARD);
            eis_seat_configure_capability(f->seat, EIS_DEVICE_CAP_BUTTON);
            eis_seat_configure_capability(f->seat, EIS_DEVICE_CAP_SCROLL);
            eis_seat_add(f->seat);
            break;
        }
        case EIS_EVENT_SEAT_BIND: {
            f->device = eis_seat_new_device(f->seat);
            eis_device_configure_name(f->device, "test-combined-device");
            eis_device_configure_capability(f->device, EIS_DEVICE_CAP_POINTER_ABSOLUTE);
            eis_device_configure_capability(f->device, EIS_DEVICE_CAP_KEYBOARD);
            eis_device_configure_capability(f->device, EIS_DEVICE_CAP_BUTTON);
            eis_device_configure_capability(f->device, EIS_DEVICE_CAP_SCROLL);
            /* Two regions with a real hole between them. */
            for (int i = 0; i < 2; i++) {
                struct eis_region *region = eis_device_new_region(f->device);
                eis_region_set_offset(region, i * 1200, 0);
                eis_region_set_size(region, 1000, 800);
                eis_region_add(region); eis_region_unref(region);
            }
            eis_device_add(f->device); eis_device_resume(f->device);
            break;
        }
        case EIS_EVENT_POINTER_MOTION_ABSOLUTE:
            f->x = eis_event_pointer_get_absolute_x(event); f->y = eis_event_pointer_get_absolute_y(event); f->events++; break;
        case EIS_EVENT_KEYBOARD_KEY:
            f->keys_down += eis_event_keyboard_get_key_is_press(event) ? 1 : -1; f->events++; break;
        case EIS_EVENT_BUTTON_BUTTON:
            f->buttons_down += eis_event_button_get_is_press(event) ? 1 : -1; f->events++; break;
        default: break;
        }
        eis_event_unref(event);
    }
}

int fixture_keys(struct fixture *f) { return f->keys_down; }
int fixture_buttons(struct fixture *f) { return f->buttons_down; }
double fixture_x(struct fixture *f) { return f->x; }
double fixture_y(struct fixture *f) { return f->y; }
void fixture_pause(struct fixture *f) { if (f->device) eis_device_pause(f->device); }
void fixture_close(struct fixture *f) {
    if (f->device) eis_device_unref(f->device);
    if (f->seat) eis_seat_unref(f->seat);
    eis_unref(f->eis); free(f);
}
