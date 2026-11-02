/* Research adapter for upstream vgmstream utkdec.c; no Workshop codec code is linked.
 * gcc -O2 -I <upstream> microtalk-decode.c <upstream>/utkdec.c -o mt-reference.exe
 * mt-reference input.utk|input.schl output.pcm
 * Pass --rev3 for revision-3 native framing. Native mode accepts only mono,
 * little-endian SCHl
 * examples selected by runtime-research. It is not a general game-audio decoder.
 */
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "utkdec.h"

static uint32_t le32(const uint8_t *p) { return (uint32_t)p[0] | (uint32_t)p[1]<<8 | (uint32_t)p[2]<<16 | (uint32_t)p[3]<<24; }
static int decode(utk_context_t *ctx, FILE *out, uint32_t count) {
    while (count) {
        int got = utk_decode_frame(ctx), i;
        if (got <= 0) return 0;
        float *samples = utk_get_samples(ctx);
        uint32_t take = count < (uint32_t)got ? count : (uint32_t)got;
        for (i=0; i<(int)take; i++) {
            float x = samples[i]; int y = x >= 0 ? (int)(x+0.5f) : (int)(x-0.5f);
            if (y < -32768) y=-32768; if (y > 32767) y=32767;
            fputc(y & 255,out); fputc((y>>8)&255,out);
        }
        count-=take;
    }
    return !ferror(out);
}

int main(int argc, char **argv) {
    FILE *in, *out; long size; uint8_t *data; int ok=1;
    utk_context_t *ctx;
    if (argc!=3 && argc!=4) return 2;
    in=fopen(argv[1],"rb"); if (!in) return 3;
    fseek(in,0,SEEK_END); size=ftell(in); rewind(in);
    if (size<32 || size>64*1024*1024) { fclose(in); return 4; }
    data=calloc((size_t)size+32,1); if (!data) return 5;
    if (fread(data,1,size,in)!=(size_t)size) return 6; fclose(in);
    out=fopen(argv[2],"wb"); if (!out) return 7;
    ctx=utk_init(argc==4 && !strcmp(argv[3],"--rev3") ? UTK_EA_PCM : UTK_EA); if (!ctx) return 8;
    if (!memcmp(data,"UTM0",4)) {
        utk_set_buffer(ctx,data+32,(size_t)size-32);
        ok=decode(ctx,out,le32(data+4)/2);
    } else if (!memcmp(data,"SCHl",4)) {
        size_t at=0;
        while (at+8 <= (size_t)size) {
            uint32_t length=le32(data+at+4);
            if (length<8 || length>(size_t)size-at) {ok=0;break;}
            if (!memcmp(data+at,"SCDl",4)) {
                if (length<17 || le32(data+at+12)!=0) {ok=0;break;}
                utk_set_buffer(ctx,data+at+17,length-17);
                if (!decode(ctx,out,le32(data+at+8))) {ok=0;break;}
            }
            at+=length;
        }
    } else ok=0;
    utk_free(ctx); free(data); fclose(out); return ok?0:9;
}
