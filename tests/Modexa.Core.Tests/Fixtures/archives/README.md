# Archive regression fixture

`encrypted-stored.rar` contains only synthetic test data created for this project. Its test-only
password is `fixture-password`. It was created with RAR 7.23 using `-ma5 -m0 -s -r`.

The files are:

- `bin/x64/aligned.bin`: bytes 0 through 31.
- `bin/x64/unaligned.bin`: 131091 bytes, with byte at index i equal to i modulo 251.
- `r6/scripts/empty.reds`: an empty file.
- `r6/scripts/sample.reds`: UTF-8 `module Example\r\n// sample\r\n`, with actual CRLF line endings.

The large unaligned stored file reproduces trailing encrypted block padding in the streaming
extractor. Tests compare the complete extracted bytes and exercise Cyberpunk installation/removal.
