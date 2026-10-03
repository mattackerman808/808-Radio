// 808 Radio audio output shim: a thin C API over miniaudio (CoreAudio on macOS), called from .NET through P/Invoke.
// The .NET side owns the ring buffer and resampling; this just pulls interleaved float32 frames on the device thread.
#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define R808AUDIO_API __attribute__((visibility("default")))

typedef struct r808audio r808audio_t;

/// Fill `frames` frames of interleaved float32 into `out` (channels per frame as opened). Called on the audio thread.
typedef void (*r808audio_read_cb)(float *out, uint32_t frames, void *user);

/// Opens the default output device at `rate` Hz with `channels` channels and starts it. Follows the system default
/// device when it changes. Returns 0 on success, or a negative miniaudio error code.
R808AUDIO_API int r808audio_open(uint32_t rate, uint32_t channels, uint32_t period_ms, r808audio_read_cb cb, void *user, r808audio_t **out);
/// The current device's name (UTF-8), valid until close; "" if unknown.
R808AUDIO_API const char *r808audio_device_name(r808audio_t *a);
/// Stops and frees.
R808AUDIO_API void r808audio_close(r808audio_t *a);
/// miniaudio's version string.
R808AUDIO_API const char *r808audio_version(void);

#ifdef __cplusplus
}
#endif
