/* Isolated compositor transport fixture. It cannot capture any physical input. */
#include <libeis.h>
#include <stdint.h>
#include <stdlib.h>

struct capture_fixture { struct eis *eis; struct eis_seat *seat; struct eis_device *device; int keymap_fd; size_t keymap_size; };

struct capture_fixture *capture_fixture_open(int *fd, int keymap_fd, uint32_t size) {
    struct capture_fixture *f = calloc(1, sizeof(*f));
    if (!f) return NULL;
    f->keymap_fd = keymap_fd; f->keymap_size = size;
    f->eis = eis_new(NULL);
    if (!f->eis || eis_setup_backend_fd(f->eis) != 0) { if (f->eis) eis_unref(f->eis); free(f); return NULL; }
    *fd = eis_backend_fd_add_client(f->eis);
    return f;
}

void capture_fixture_poll(struct capture_fixture *f) {
    eis_dispatch(f->eis);
    struct eis_event *event;
    while ((event = eis_get_event(f->eis))) {
        switch (eis_event_get_type(event)) {
        case EIS_EVENT_CLIENT_CONNECT: {
            struct eis_client *client = eis_event_get_client(event); eis_client_connect(client);
            f->seat = eis_client_new_seat(client, "isolated-capture-seat");
            eis_seat_configure_capability(f->seat, EIS_DEVICE_CAP_POINTER);
            eis_seat_configure_capability(f->seat, EIS_DEVICE_CAP_KEYBOARD);
            eis_seat_configure_capability(f->seat, EIS_DEVICE_CAP_BUTTON);
            eis_seat_configure_capability(f->seat, EIS_DEVICE_CAP_SCROLL); eis_seat_add(f->seat); break;
        }
        case EIS_EVENT_SEAT_BIND: {
            if (f->device) break;
            f->device = eis_seat_new_device(f->seat); eis_device_configure_name(f->device, "isolated-capture-device");
            eis_device_configure_capability(f->device, EIS_DEVICE_CAP_POINTER);
            eis_device_configure_capability(f->device, EIS_DEVICE_CAP_KEYBOARD);
            eis_device_configure_capability(f->device, EIS_DEVICE_CAP_BUTTON);
            eis_device_configure_capability(f->device, EIS_DEVICE_CAP_SCROLL);
            struct eis_keymap *map = eis_device_new_keymap(f->device, EIS_KEYMAP_TYPE_XKB, f->keymap_fd, f->keymap_size);
            eis_keymap_add(map); eis_keymap_unref(map);
            eis_device_add(f->device); eis_device_resume(f->device); break;
        }
        default: break;
        }
        eis_event_unref(event);
    }
}

void capture_fixture_start(struct capture_fixture *f, uint32_t sequence) { eis_device_start_emulating(f->device, sequence); }
void capture_fixture_stop(struct capture_fixture *f) { eis_device_stop_emulating(f->device); }
void capture_fixture_motion(struct capture_fixture *f, double x, double y) { eis_device_pointer_motion(f->device, x, y); }
void capture_fixture_key(struct capture_fixture *f, uint32_t code, int down) { eis_device_keyboard_key(f->device, code, down); }
void capture_fixture_button(struct capture_fixture *f, uint32_t code, int down) { eis_device_button_button(f->device, code, down); }
void capture_fixture_modifiers(struct capture_fixture *f, uint32_t depressed) { eis_device_keyboard_send_xkb_modifiers(f->device, depressed, 0, 0, 0); }
void capture_fixture_scroll(struct capture_fixture *f) { eis_device_scroll_delta(f->device, 0, -15); eis_device_scroll_discrete(f->device, 0, -120); }
void capture_fixture_frame(struct capture_fixture *f) { eis_device_frame(f->device, 1000); }
void capture_fixture_close(struct capture_fixture *f) {
    if (f->device) { eis_device_remove(f->device); eis_device_unref(f->device); }
    if (f->seat) eis_seat_unref(f->seat);
    eis_unref(f->eis); free(f);
}
