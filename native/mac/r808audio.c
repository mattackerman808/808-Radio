// See r808audio.h. miniaudio is vendored (miniaudio.h, MIT-0 / public domain).
#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE
#define MA_NO_RUNTIME_LINKING   // link the frameworks directly rather than dlopen() them (cleaner for signing / sandbox)
#define MA_API static
#include "miniaudio.h"
#include "r808audio.h"
#include <stdlib.h>
#include <string.h>

struct r808audio {
    ma_context context;
    ma_device device;
    r808audio_read_cb cb;
    void *user;
    char name[256];
};

static void data_callback(ma_device *dev, void *out, const void *in, ma_uint32 frames)
{
    (void)in;
    r808audio_t *a = (r808audio_t *)dev->pUserData;
    a->cb((float *)out, frames, a->user);
}

int r808audio_open(uint32_t rate, uint32_t channels, uint32_t period_ms, r808audio_read_cb cb, void *user, r808audio_t **out)
{
    *out = NULL;
    r808audio_t *a = (r808audio_t *)calloc(1, sizeof *a);
    if (!a) return MA_OUT_OF_MEMORY;
    a->cb = cb;
    a->user = user;

    ma_context_config cc = ma_context_config_init();
    cc.coreaudio.sessionCategory = ma_ios_session_category_none;
    ma_result r = ma_context_init(NULL, 0, &cc, &a->context);
    if (r != MA_SUCCESS) { free(a); return r; }

    ma_device_config dc = ma_device_config_init(ma_device_type_playback);
    dc.playback.format = ma_format_f32;
    dc.playback.channels = channels;
    dc.sampleRate = rate;
    dc.periodSizeInMilliseconds = period_ms;
    dc.dataCallback = data_callback;
    dc.pUserData = a;
    dc.noPreSilencedOutputBuffer = MA_TRUE;   // the callback always fills the whole buffer
    // playback.pDeviceID == NULL: the system default device, and miniaudio re-routes when the default changes
    r = ma_device_init(&a->context, &dc, &a->device);
    if (r != MA_SUCCESS) { ma_context_uninit(&a->context); free(a); return r; }
    ma_device_get_name(&a->device, ma_device_type_playback, a->name, sizeof a->name, NULL);
    r = ma_device_start(&a->device);
    if (r != MA_SUCCESS) { ma_device_uninit(&a->device); ma_context_uninit(&a->context); free(a); return r; }
    *out = a;
    return 0;
}

const char *r808audio_device_name(r808audio_t *a)
{
    if (!a) return "";
    ma_device_get_name(&a->device, ma_device_type_playback, a->name, sizeof a->name, NULL);
    return a->name;
}

void r808audio_close(r808audio_t *a)
{
    if (!a) return;
    ma_device_uninit(&a->device);
    ma_context_uninit(&a->context);
    free(a);
}

const char *r808audio_version(void) { return ma_version_string(); }
