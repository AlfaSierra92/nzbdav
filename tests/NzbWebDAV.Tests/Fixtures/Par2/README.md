# PAR2 engine test fixtures

A small, deterministic PAR2 recovery set used to validate the GF(2^16) Reed-Solomon
engine (`backend/Par2Recovery`).

- `data.bin` — 64 KiB deterministic input (`byte[i] = (i*73 + 19) % 256`), 16 slices of 4096 bytes.
- `data.par2` — index (Main + FileDesc + IFSC + Creator).
- `data.vol*.par2` — recovery volumes carrying exponents 0..5 (6 recovery slices).

Regenerate with par2cmdline / par2cmdline-turbo:

```
par2 create -s4096 -c6 data.par2 data.bin
```

The tests delete known input slices and confirm the engine reconstructs them
byte-for-byte and that each reconstructed slice matches its IFSC MD5.
