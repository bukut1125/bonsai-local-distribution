# Third-party components included by a Bonsai install

- PrismML `llama.cpp` Windows runtime, pinned to revision `9a9394a895b96003ca842a6041cb28ac49a108f7`: MIT License. The setup downloads the selected CUDA or CPU release directly from PrismML's GitHub release.
- Bonsai 2 27B PTQ1_0 GGUF, pinned to Hugging Face revision `6ed5e12bf84b7a63069882c91dd9e9218647d17b`: Apache-2.0. The setup downloads the model directly from the publisher's Hugging Face repository.
- OrcaRouter Bonsai LoRA, pinned to commit `947a80cd1d3b4f9a97417025e6c2c62223571287`: Apache-2.0. The setup downloads the adapter directly from the publisher's GitHub repository.

The installer puts copies of the applicable license texts under `Bonsai\licenses`. See `licenses\MIT.txt` and `licenses\Apache-2.0.txt` in this source project.
