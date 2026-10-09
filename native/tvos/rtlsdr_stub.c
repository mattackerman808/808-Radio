// librtlsdr stand-in for tvOS: libnrsc5 (and RtlSdrNative.cs) link against these for its own USB input path, which the Apple TV app never
// uses (there's no USB; samples come from rtl_tcp through the .NET side). Every call fails.
#include <rtl-sdr.h>

int rtlsdr_open(rtlsdr_dev_t **dev, uint32_t index) { (void)index; *dev = 0; return -1; }
int rtlsdr_close(rtlsdr_dev_t *dev) { (void)dev; return -1; }
uint32_t rtlsdr_get_center_freq(rtlsdr_dev_t *dev) { (void)dev; return 0; }
int rtlsdr_get_tuner_gain(rtlsdr_dev_t *dev) { (void)dev; return 0; }
int rtlsdr_get_tuner_gains(rtlsdr_dev_t *dev, int *gains) { (void)dev; (void)gains; return 0; }
int rtlsdr_read_async(rtlsdr_dev_t *dev, rtlsdr_read_async_cb_t cb, void *ctx, uint32_t buf_num, uint32_t buf_len)
{ (void)dev; (void)cb; (void)ctx; (void)buf_num; (void)buf_len; return -1; }
int rtlsdr_read_sync(rtlsdr_dev_t *dev, void *buf, int len, int *n_read) { (void)dev; (void)buf; (void)len; *n_read = 0; return -1; }
int rtlsdr_cancel_async(rtlsdr_dev_t *dev) { (void)dev; return -1; }
int rtlsdr_reset_buffer(rtlsdr_dev_t *dev) { (void)dev; return -1; }
int rtlsdr_set_bias_tee(rtlsdr_dev_t *dev, int on) { (void)dev; (void)on; return -1; }
int rtlsdr_set_center_freq(rtlsdr_dev_t *dev, uint32_t freq) { (void)dev; (void)freq; return -1; }
int rtlsdr_set_direct_sampling(rtlsdr_dev_t *dev, int on) { (void)dev; (void)on; return -1; }
int rtlsdr_set_freq_correction(rtlsdr_dev_t *dev, int ppm) { (void)dev; (void)ppm; return -1; }
int rtlsdr_set_offset_tuning(rtlsdr_dev_t *dev, int on) { (void)dev; (void)on; return -1; }
int rtlsdr_set_sample_rate(rtlsdr_dev_t *dev, uint32_t rate) { (void)dev; (void)rate; return -1; }
int rtlsdr_set_tuner_gain(rtlsdr_dev_t *dev, int gain) { (void)dev; (void)gain; return -1; }
int rtlsdr_set_tuner_gain_mode(rtlsdr_dev_t *dev, int manual) { (void)dev; (void)manual; return -1; }

// ... and the ones the .NET side's own USB bindings name (RtlSdrNative.cs); the AOT build links those directly too
uint32_t rtlsdr_get_device_count(void) { return 0; }
const char *rtlsdr_get_device_name(uint32_t index) { (void)index; return ""; }
int rtlsdr_get_device_usb_strings(uint32_t index, char *manufact, char *product, char *serial)
{ (void)index; if (manufact) *manufact = 0; if (product) *product = 0; if (serial) *serial = 0; return -1; }
uint32_t rtlsdr_get_sample_rate(rtlsdr_dev_t *dev) { (void)dev; return 0; }
enum rtlsdr_tuner rtlsdr_get_tuner_type(rtlsdr_dev_t *dev) { (void)dev; return RTLSDR_TUNER_UNKNOWN; }
int rtlsdr_set_agc_mode(rtlsdr_dev_t *dev, int on) { (void)dev; (void)on; return -1; }
