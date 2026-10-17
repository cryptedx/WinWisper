#!/usr/bin/env python3
"""Compile and exercise the actual C++ patch fragments, without Windows/ORT.

This checks parameter parsing and exception/lifetime behavior, not GPU support.
Run with Python 3 and clang++ (no downloaded dependencies).
"""
from pathlib import Path
import re
import subprocess
import tempfile

script = (Path(__file__).resolve().parents[1] / "eng/Build-SherpaRuntime.ps1").read_text()


def replacement(name):
    block = script.split(f"-Name '{name}'", 1)[1]
    return re.search(r"-NewValue @'\n(.*?)\n'@", block, re.S)[1]


boundary = replacement("Checked recognizer creation")
device = replacement("DirectML device index")
source = r'''
#include <cassert>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_map>
static bool valid = true, fail = false;
static int alive = 0;
namespace sherpa_onnx {
struct OfflineRecognizerConfig { bool Validate() const { return valid; } };
struct OfflineRecognizer {
  explicit OfflineRecognizer(OfflineRecognizerConfig) {
    if (fail) throw std::runtime_error("test");
  }
};
}
struct SherpaOnnxOfflineRecognizerConfig {};
struct SherpaOnnxOfflineRecognizer {
  SherpaOnnxOfflineRecognizer() { ++alive; }
  ~SherpaOnnxOfflineRecognizer() { --alive; }
  std::unique_ptr<sherpa_onnx::OfflineRecognizer> impl;
};
sherpa_onnx::OfflineRecognizerConfig GetOfflineRecognizerConfig(
    const SherpaOnnxOfflineRecognizerConfig*) { return {}; }
'''
source += boundary
source += '\nint Device(std::unordered_map<std::string, std::string> config) {\n'
source += device + '\nreturn device_id;\n}\n'
source += r'''
int main() {
  assert(Device({}) == 0);
  assert(Device({{"device_id", "1"}}) == 1);
  assert(Device({{"device_id", "3"}}) == 3);
  for (auto invalid : {"-1", "x", "", "1x", "9999999999999999999"}) {
    bool rejected = false;
    try { Device({{"device_id", invalid}}); }
    catch (const std::exception&) { rejected = true; }
    assert(rejected);
  }
  auto recognizer = SherpaOnnxCreateOfflineRecognizer(nullptr);
  assert(recognizer && alive == 1 && WinWisprGetLastRecognizerStatus() == 1);
  delete recognizer;
  std::thread other([] { assert(WinWisprGetLastRecognizerStatus() == 0); });
  other.join();
  assert(WinWisprGetLastRecognizerStatus() == 1);
  fail = true;
  assert(SherpaOnnxCreateOfflineRecognizer(nullptr) == nullptr);
  assert(alive == 0 && WinWisprGetLastRecognizerStatus() == 0);
  fail = false;
  valid = false;
  assert(SherpaOnnxCreateOfflineRecognizer(nullptr) == nullptr);
  assert(alive == 0 && WinWisprGetLastRecognizerStatus() == 0);
}
'''
with tempfile.TemporaryDirectory(prefix="winwispr-native-check-") as directory:
    root = Path(directory)
    (root / "check.cc").write_text(source)
    subprocess.run(["clang++", "-std=c++17", "-fdeclspec", "-Wno-ignored-attributes",
                    str(root / "check.cc"), "-o", str(root / "check")], check=True)
    subprocess.run([str(root / "check")], check=True)
print("Native patch boundary: PASS (GPU execution not tested)")
