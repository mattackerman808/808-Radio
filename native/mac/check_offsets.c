// Verifies the nrsc5_event_t field offsets that Radio808.Core/Native/Nrsc5Native.cs and Hd/HdDecoder.cs read by hand.
// Build: clang -I nrsc5/include native/mac/check_offsets.c -o check_offsets && ./check_offsets
#include <stdio.h>
#include <stddef.h>
#include <nrsc5.h>
#define U(member) offsetof(nrsc5_event_t, member)
#define CHECK(expr, expected) do { size_t v = (expr); printf("%-40s %3zu %s\n", #expr, v, v == (expected) ? "ok" : "MISMATCH"); if (v != (expected)) bad++; } while (0)
int main(void) {
    int bad = 0;
    CHECK(U(event), 0);
    CHECK(U(mer.lower), 8);  CHECK(U(mer.upper), 12);
    CHECK(U(ber.cber), 8);
    CHECK(U(audio.program), 8); CHECK(U(audio.data), 16); CHECK(U(audio.count), 24); CHECK(U(audio.flags), 32);
    CHECK(U(id3.program), 8); CHECK(U(id3.title), 16); CHECK(U(id3.artist), 24); CHECK(U(id3.album), 32);
    CHECK(U(id3.xhdr.mime), 64); CHECK(U(id3.xhdr.lot), 72);
    CHECK(U(lot.lot), 12); CHECK(U(lot.size), 16); CHECK(U(lot.name), 24); CHECK(U(lot.data), 32);
    CHECK(U(lot.service), 48); CHECK(U(lot.component), 56);
    CHECK(U(audio_service.program), 8); CHECK(U(audio_service.type), 16);
    CHECK(U(station_name.name), 8); CHECK(U(station_slogan.slogan), 8); CHECK(U(station_message.message), 8);
    CHECK(U(emergency_alert.message), 8);
    CHECK(U(here_image.image_type), 8); CHECK(U(here_image.n1), 16); CHECK(U(here_image.name), 48);
    CHECK(U(here_image.size), 56); CHECK(U(here_image.data), 64);
    CHECK(offsetof(nrsc5_sig_component_t, data.mime), 20);
    CHECK(offsetof(nrsc5_sig_service_t, type), 8);
    CHECK(offsetof(nrsc5_sig_service_t, number), 10);
    CHECK(offsetof(nrsc5_sig_service_t, audio_component), 32);
    CHECK(offsetof(nrsc5_sig_component_t, audio.port), 12);
    CHECK(sizeof(void*), 8);
    printf(bad ? "%d MISMATCHES\n" : "all offsets match the C# bindings\n", bad);
    return bad != 0;
}
