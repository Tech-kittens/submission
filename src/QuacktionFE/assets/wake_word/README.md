# Hey Quacktion keyword model

Source: k2-fsa/sherpa-onnx release `kws-models`, model
`sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20`, chunk-16 / left-64.

Download: https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20.tar.bz2

Original author/model card:
https://modelscope.cn/models/pkufool/icefall-kws-zipformer-zh-en-3M-2025-12-20
The original README declares Apache License 2.0; a copy is included as
UPSTREAM-MODEL-CARD.md. LICENSE contains Apache-2.0. Model weights are unchanged.

Bundled files are the int8 encoder and joiner, fp32 decoder, and tokens.txt.
They have shorter local filenames; encoder/decoder/joiner use epoch-13-avg-2.
Approximately 5.2 MB of model assets are bundled for offline inference.

keywords.txt is our pronunciation vocabulary for "Hey Quacktion":
HH EY1 + K W AE1 K SH AH0 N, with a stressed final-syllable variant.
Pronunciations use the model's English phone vocabulary. Score 1.5, threshold
0.25, one CPU thread. These are starting settings, not a measured accuracy claim.

Documentation: https://k2-fsa.github.io/sherpa/onnx/kws/pretrained_models/index.html
