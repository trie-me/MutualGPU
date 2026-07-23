import importlib.util
import io
import unittest
from pathlib import Path


def load_inference_module():
    path = Path(__file__).parents[1] / "src" / "inference.py"
    spec = importlib.util.spec_from_file_location("mutualgpu_flux2_inference", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class InferenceStartupTests(unittest.TestCase):
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
