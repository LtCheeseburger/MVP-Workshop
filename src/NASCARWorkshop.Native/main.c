/* Isolated 32-bit EA Madden decoder host. Protocol: u32 input size, u32
   output size, input bytes; response: i32 decoded size, decoded bytes.
   Decoder by Luigi Auriemma, distributed under QuickBMS's GPL-2.0 license. */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <io.h>
#include <fcntl.h>
#include "third_party/ea_madden.c"

int main(void) {
    uint32_t sizes[2];
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    while (fread(sizes, sizeof(sizes), 1, stdin) == 1) {
        if (!sizes[0] || sizes[0] > 268435456 || !sizes[1] || sizes[1] > 268435456) return 2;
        unsigned char *in = calloc((size_t)sizes[0] + 65536, 1);
        unsigned char *out = calloc((size_t)sizes[1] + 65536, 1);
        if (!in || !out || fread(in, 1, sizes[0], stdin) != sizes[0]) return 3;
        int result = -1;
        __try { result = EA_Madden_decompress(in, sizes[0], out, sizes[1]); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return 4; }
        if (result != (int)sizes[1]) result = -1;
        if (fwrite(&result, 4, 1, stdout) != 1) return 5;
        if (result > 0 && fwrite(out, 1, result, stdout) != (size_t)result) return 5;
        fflush(stdout);
        free(in); free(out);
    }
    return 0;
}
