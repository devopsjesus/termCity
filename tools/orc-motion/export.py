#!/usr/bin/env python3
"""Bake an original local WebGL orc study into native Godot preview tracks."""
import argparse
import base64
from html.parser import HTMLParser
import json
import hashlib
from pathlib import Path
import subprocess
import tempfile


class ResultParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.active = False
        self.result = ""

    def handle_starttag(self, tag, attrs):
        if tag == "pre" and dict(attrs).get("id") == "result":
            self.active = True

    def handle_endtag(self, tag):
        if tag == "pre":
            self.active = False

    def handle_data(self, data):
        if self.active:
            self.result += data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("study", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--chrome", default="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome")
    args = parser.parse_args()
    study = args.study.resolve(strict=True)
    script = Path(__file__).with_name("export.js").read_text()
    with tempfile.TemporaryDirectory(prefix="termcity-orc-bake-") as folder:
        root = Path(folder)
        harness = root / "export.html"
        harness.write_text(
            '<!doctype html><meta charset="utf-8"><pre id="result">PENDING</pre>'
            '<iframe id="study"></iframe><script>'
            'const frame=document.getElementById("study");'
            'frame.onload=async()=>{try{const result=await frame.contentWindow.eval('
            + json.dumps(script) + ');document.getElementById("result").textContent=JSON.stringify(result);'
            '}catch(error){document.getElementById("result").textContent=JSON.stringify({error:String(error)});}};'
            'frame.src=' + json.dumps(study.as_uri()) + ';</script>'
        )
        result = subprocess.run([
            args.chrome, "--headless=new", "--no-first-run", "--no-default-browser-check",
            "--disable-extensions", "--disable-background-networking", "--disable-component-update",
            "--disable-sync", "--allow-file-access-from-files", "--use-angle=swiftshader",
            "--enable-unsafe-swiftshader", "--user-data-dir=" + str(root / "profile"),
            "--virtual-time-budget=100000", "--timeout=180000", "--dump-dom", harness.as_uri()
        ], capture_output=True, text=True, timeout=210, check=True)
        output = ResultParser()
        output.feed(result.stdout)
        try:
            payload = json.loads(output.result)
        except json.JSONDecodeError as error:
            raise RuntimeError("Browser export did not finish: " + output.result[:200] +
                               "\n" + result.stderr[-1000:]) from error
        if "error" in payload:
            raise RuntimeError(payload["error"])
        data = base64.b64decode(payload.pop("data"), validate=True)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_bytes(data)
        payload["sourceSha256"] = hashlib.sha256(study.read_bytes()).hexdigest()
        payload["assetSha256"] = hashlib.sha256(data).hexdigest()
        args.output.with_suffix(".json").write_text(json.dumps(payload, indent=2) + "\n")
        print(json.dumps(payload, indent=2))


if __name__ == "__main__":
    main()
