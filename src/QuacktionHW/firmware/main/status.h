#pragma once

typedef enum {
    STATUS_BOOT,
    STATUS_CONNECTING,
    STATUS_IDLE,
    STATUS_LISTENING,
    STATUS_SPEAKING,
    STATUS_MUTED,
    STATUS_SETUP,
} status_t;

void status_set(status_t status);
/* Shows the current status again, after something else drew on the LEDs. */
void status_refresh(void);
/* Blinks red, then restores the previous status. Blocks for about 1.5 s. */
void status_show_error(void);
