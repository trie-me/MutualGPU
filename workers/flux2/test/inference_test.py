import importlib.util
import io
import unittest
from pathlib import Path

from PIL import Image


def load_inference_module():
    path = Path(__file__).parents[1] / "src" / "inference.py"
    spec = importlib.util.spec_from_file_location("mutualgpu_flux2_inference", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class InferenceStartupTests(unittest.TestCase):
    def test_preview_is_display_sized_and_within_the_upload_budget(self):
        inference = load_inference_module()
        image = Image.effect_noise((1024, 1024), 100).convert("RGB")

        encoded = inference.encode_preview(image)

        self.assertLessEqual(len(encoded), inference.PREVIEW_TARGET_BYTES)
        self.assertEqual(encoded[:8], b"\x89PNG\r\n\x1a\n")
        with Image.open(io.BytesIO(encoded)) as preview:
            self.assertLessEqual(max(preview.size), inference.PREVIEW_MAX_DIMENSION)

    def test_serve_warms_model_and_safety_checker_before_ready(self):
        inference = load_inference_module()
        events = []
        inference.initialize = lambda: events.append("initialize")
        inference.load_pipeline = lambda: events.append("model")
        inference.load_safety_checker = lambda: events.append("safety")
        inference.emit = lambda message: events.append(message["type"])
        inference.log = lambda _message: None
        inference.sys.stdin = io.StringIO("")

        inference.serve()

        self.assertEqual(events, ["initialize", "model", "safety", "ready"])

    def test_model_load_failure_category_walks_the_cause_chain(self):
        inference = load_inference_module()
        try:
            try:
                raise OSError("accelerator out of memory")
            except OSError as cause:
                raise RuntimeError("FLUX.2 model could not be loaded") from cause
        except RuntimeError as error:
            self.assertEqual(inference.safe_category(error), "GpuOutOfMemory")

    def test_safety_checker_load_failure_has_a_specific_category(self):
        inference = load_inference_module()
        self.assertEqual(
            inference.safe_category(RuntimeError("FLUX.2 safety checker could not be loaded")),
            "SafetyCheckerLoadFailed",
        )


if __name__ == "__main__":
    unittest.main()
