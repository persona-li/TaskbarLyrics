# Native lyric compatibility fixtures

`matching-reference.json` records the exact scores, high-confidence classifications,
best candidate and automatic binding decisions from the original deterministic
`TrackMatchWorkflowSmoke` matrices. It includes multilingual titles, inherited
duration, duplicate albums, artist credit boundaries, album language siblings,
featured releases and ambiguous recordings. These are public/synthetic harness
inputs, not the user's settings or cache.

`qrc-reference.json` contains twelve ciphertexts produced by the original C#
DESHelper and .NET zlib encoder: stored, fixed/dynamic compression, multilingual
words, nested XML and long repeated text. The C++ implementation must decrypt
each ciphertext and preserve real word timestamps.

These fixtures are frozen compatibility references. Update them only after
reviewing an intentional domain change. Native tests also use an injected
transport and isolated temporary storage to verify cache fast paths, manual
priority, missing lyric retry, transport failure and cancellation. No desktop,
personal data or network is used by the offline target.
