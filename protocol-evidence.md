# Static evidence for Motronic Diagnost 1.21 / Motronic M1.5

Version 0.2.1 adds only decoding of fields in the existing F4. The complete original list contains 13 numeric graphs; details and instruction offsets are in `analysis/M1.5-F4-EE-fields.txt`. The byte transport, initialization, serial settings and request allowlist remain unchanged. The new recorder selects the CO/lambda interpretation of shared byte F4[8] according to configuration bit F4[11]&0x20; this is a recorder policy, not an original numeric-handler guard and not proof of physical sensor presence. Logs written by 0.2.1 use schema 2; legacy schema 1 stays readable without synthesizing missing fields.

Source: `Motronic.crack/Motronic.crack/Motronic.exe`, SHA-256 `46f5099d0970294115bbd504e3f6bb17946cec3cdb3c6519442b6ec759320f9a`. Original EXE was not executed; no hardware access was made. VA addresses below use image base 0x400000. In this image raw file offset equals VA minus 0x400000.

## Scope and confidence

The operations below were recovered from x86 instructions and Delphi resources. They have not been validated against an ECU or adapter. Matching original code does not establish physical compatibility or prove risk-free operation. The new recorder must never expose arbitrary packet sending, error erasure, actuator tests, or memory programming.

## M1.5 connection

- Port configuration in DFM: custom baud, 8 data bits, no parity, one stop bit; RTS disabled, DTR disabled initially.
- VA 0x54F07C sets custom baud from its argument, opens the port, clears receive and transmit buffers, then asserts DTR. VA 0x54F1C0 chooses 9600 baud for M1.5 (type 1); the 8861-baud path is type 0 / ML4.1.
- Caller clears both buffers again and waits 1000 ms.
- VA 0x54F100 sets BREAK for 1000 ms, clears BREAK for 200 ms, sets BREAK for 600 ms, clears BREAK, clears RX and TX buffers, then waits 100 ms.
- VA 0x54F180 reads one raw byte and requires 0x55.
- VA 0x54F494 onward reads keyword 1, writes its bitwise inverse, reads own echo; reads keyword 2, writes its inverse, reads own echo. Original code does not validate specific keyword values. It does not wait for both keywords before acknowledging the first.
- No additional address-complement stage was found in the original connection path.
- BREAK helper VA 0x49B5AC invokes EscapeCommFunction with SETBREAK (8) / CLRBREAK (9). DTR helper VA 0x49B5F0 uses SETDTR (5) / CLRDTR (6). Buffer clear helper VA 0x49B634 uses PurgeComm RXCLEAR (8) / TXCLEAR (4).
- The BREAK waveform is consistent with 5-baud address 0x10; that label is an inference. The timings above are directly recovered.

## Frames and handshake

Complete wire frame: `[L, counter, title, payload..., 0x03]`, total byte count `L + 1`; payload byte count `L - 3`. The terminator is at frame index L.

- Receive byte VA 0x549398: read data byte b, send b XOR 0xFF, consume the adapter echo of the sent inverse.
- Receive frame VA 0x5493FC: receive with the above handshake the first L bytes including L itself, then read one raw terminator byte without inverse acknowledgment.
- Send byte VA 0x549480: write b, read own echo, read peer inverse acknowledgment.
- Send frame VA 0x5494D0: send the first L bytes using that handshake, then write the terminator and consume its echo; no peer acknowledgment for the terminator.
- Original helpers do not validate echo, inverse acknowledgment, or terminator equality. A new transport may reject mismatches rather than continue using corrupt frames.
- VA 0x5495A4 takes the incoming counter and increments it modulo 256 for the next outgoing frame; no fixed starting counter was found.
- Original connection enqueues ACK template `[03, counter, 09, 03]` before starting threads. Worker then receives a frame first and sends the queued ACK. ECU identification response title F6 is decoded. The number of initial identification frames is not hardcoded in the inspected path; no exact startup-complete marker has yet been established from it.
- There is no explicit Sleep or fixed sampling interval in the frame loop. TGalaContainerChannel sends use infinite synchronization waits. ECU, serial, USB, and processing determine actual cadence.
- A failed significant read causes disconnect handling and a 1000-ms wait, not an automatic retry or a sample interval. Serial timeout must not be described as sampling frequency.

## Read-only M1.5 polling

VA 0x54D932 (M1.5 branch of btPlayClick) clears its earlier query list and selects these persistent templates:

- VA 0x555684: `[03, counter, 12, 03]` for primary parameters, including RPM. Response F4 is decoded at VA 0x549DD8.
- VA 0x5556B0: `[03, counter, 1C, 03]` for injection duration / adsorber. Response EE is decoded in the same method.
- ACK: `[03, counter, 09, 03]`.

The request-to-response association follows native query selection and response contents. Commands 08 and address-based 01 are used in the ML4.1 polling branch, not the consolidated M1.5 branch.

## F4 decoding

Indexes below are in the full frame: frame[0] is L, frame[1] counter, frame[2] F4. A full-frame length of at least 17 bytes is needed for the last listed field. Preserve raw frames and byte values along with decoded values.

| Field | Exact original conversion | Code VA |
| --- | --- | --- |
| RPM | frame[13] * 40 | 0x549E87 |
| Air flow sensor voltage | frame[3] * 5.0 / 256.0 | 0x549F21 |
| Battery voltage | frame[4] / 15.0 + 0.4 | 0x549EDE |
| Intake air temperature C | TempTable[frame[5]] | 0x54A051 |
| Coolant temperature C | TempTable[frame[6]] | 0x54A028 |
| CO potentiometer V | frame[8] * 5.0 / 256.0 | 0x549F5F |
| Throttle position sensor V | frame[10] * 5.0 / 256.0 | 0x54A0EF |
| Ignition angle deg | IgnitionTable[frame[15]] | 0x549EB5 |

RPM is ECU-reported with 40-rpm resolution, not a crankshaft waveform. The temperature and ignition lookups are literal int32 values in the source image; their physical calibration has not been validated independently. In particular, IgnitionTable[0] is literally 10 in this EXE.

## EE decoding

- Injection milliseconds: `(((frame[4] & 0x7F) << 8) | frame[5]) / 500.0`, VA 0x54A520.
- Adsorber duty percent: `frame[9] * 100.0 / 256.0`, VA 0x54A568.
- A complete frame length of at least 11 bytes is needed for both fields.
- Primary F4 and secondary EE are acquired in separate frames. Original graphs share a cycle index; record individual receive timestamps instead of claiming simultaneous acquisition.

## Exact tables

TempTable: 256 signed little-endian int32 values at VA 0x554910, raw offset 0x154910:

```json
[197,194,191,189,186,184,181,178,176,173,171,168,165,163,160,158,155,152,150,147,145,142,139,137,134,132,129,126,124,121,119,116,114,112,111,110,109,108,107,106,105,104,103,102,101,100,99,98,97,95,94,93,92,91,90,89,88,87,86,85,84,83,82,81,80,79,78,78,77,77,76,76,75,74,74,73,73,72,72,71,71,70,69,69,68,68,67,67,66,65,65,64,64,63,63,62,62,61,61,60,60,59,59,58,58,57,57,56,56,55,55,54,54,53,53,52,52,51,51,50,50,49,49,48,48,47,47,46,46,45,44,44,43,43,42,42,41,41,40,40,39,39,38,38,37,36,36,35,35,34,34,33,33,32,32,31,31,30,30,29,29,28,28,27,27,26,26,25,25,24,24,23,23,22,22,21,21,20,20,19,19,18,18,17,17,17,16,16,15,15,14,14,13,13,12,12,11,11,10,10,9,9,8,8,7,7,6,6,6,5,4,3,3,2,1,0,0,0,-1,-2,-3,-3,-4,-5,-6,-6,-7,-8,-9,-10,-10,-11,-12,-13,-14,-14,-15,-16,-17,-18,-19,-20,-21,-22,-24,-25,-26,-28,-29,-30,-32,-33,-34,-36,-37,-38]
```

IgnitionTable: 256 signed little-endian int32 values at VA 0x554D10, raw offset 0x154D10:

```json
[10,108,107,106,105,105,104,103,102,102,101,100,99,99,98,97,96,96,95,94,93,93,92,91,90,90,89,88,87,87,86,85,84,84,83,82,81,81,80,79,78,78,77,76,75,75,74,73,72,72,71,70,69,69,68,67,66,66,65,64,63,63,62,61,60,60,59,58,57,57,56,55,54,54,53,52,51,51,50,49,48,48,47,46,45,45,44,43,42,42,41,40,39,39,38,37,36,36,35,34,33,33,32,31,30,30,29,28,27,27,26,25,24,24,23,22,21,21,20,19,18,18,17,16,15,15,14,13,12,12,11,10,9,9,8,7,6,6,5,4,3,3,2,1,0,0,-1,-2,-3,-3,-4,-5,-6,-6,-7,-8,-9,-9,-10,-11,-12,-12,-13,-14,-15,-15,-16,-17,-18,-18,-19,-20,-21,-21,-22,-23,-24,-24,-25,-26,-27,-27,-28,-29,-30,-30,-31,-32,-33,-33,-34,-35,-36,-36,-37,-38,-39,-39,-40,-41,-42,-42,-43,-44,-45,-45,-46,-47,-48,-48,-49,-50,-51,-51,-52,-53,-54,-54,-55,-56,-57,-57,-58,-59,-60,-60,-61,-62,-63,-63,-64,-65,-66,-66,-67,-68,-69,-69,-70,-71,-72,-72,-73,-74,-75,-75,-76,-77,-78,-78,-79,-80,-81,-81,-82,-83]
```

## Excluded operations

- Error erase template VA 0x555474 sends command 05 and is referenced by btErrClearClick only in the inspected static references.
- Actuator templates VA 0x5557B8 through 0x5558C0 send command 04. They are used by btOnClick. M1.5 visible actuator arguments are 10 (injector), 05 (adsorber), 04 (IAC). They must not exist in the recorder transport allowlist.
- ROM dump template VA 0x5558EC sends command 03 with count and address. It reads 32768 bytes for M1.5 using 1024 requests of 32 bytes, addresses 0x2000 through 0x9FFF. Response FD copies payload into host RAM at 0x556E58 (handler VA 0x54A748). Saving writes the host buffer to a PC file. This proves the observed dump path reads; it does not establish a distinct EEPROM-reading or memory-programming feature.
