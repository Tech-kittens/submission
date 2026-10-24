idf_component_register(SRCS "audio_in.c" "app.c" "board.c" "audio_out.c" "pcm_ring.c" "buttons.c" "status.c"
                            "net_wifi.c" "session.c" "session_audio.c" "device_socket.c"
                            "config_store.c" "identity.c" "setup.c"
                    INCLUDE_DIRS "."
                    EMBED_FILES "quack.wav")
