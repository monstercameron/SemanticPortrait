"""Persistent voice server for SemanticPortrait.

Loads the WhisperToMe STT/TTS models ONCE and serves requests over stdin/stdout —
the per-call CLI paid the full model load (~5-10s) on every dictation chunk. Runs
inside the sibling whispertome project's venv:

    <whispertome>/.venv/Scripts/python.exe sp_voice_server.py <whispertome-root>

Protocol: one JSON object per line, one JSON reply per line.
    {"op":"ping"}                                -> {"ok":true,"ready":true}
    {"op":"stt","wav":"C:\\u.wav"}               -> {"ok":true,"text":"..."}
    {"op":"tts","text":"...","out":"C:\\o.wav"}  -> {"ok":true,"out":"C:\\o.wav"}
    {"op":"voice_status"}                        -> {"ok":true,"missing":[{"name":..,"mb":..}]}
    {"op":"download_models"}                     -> streams {"progress":..} lines, then {"ok":..}
    {"op":"quit"}                                -> exits

Models lazy-load on first use of each op (an STT-only session never loads TTS).
Errors reply {"ok":false,"error":"..."} and the server keeps serving.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(sys.argv[1]).resolve()
sys.path.insert(0, str(PROJECT_ROOT / "src"))

# Config load is cheap; models are the expensive part and load lazily below.
from whispertome.config import load_config  # noqa: E402

_config = load_config(PROJECT_ROOT, require_openai_key=False)
_stt = None
_tts = None


def _reply(obj: dict) -> None:
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def _do_stt(wav: str) -> dict:
    global _stt
    from whispertome.cli import load_audio_buffer_from_wav, prepare_stt_model

    if _stt is None:
        _stt = prepare_stt_model(_config, allow_non_npu=False)
    audio = load_audio_buffer_from_wav(Path(wav))
    transcript = _stt.transcribe(audio)
    return {"ok": True, "text": transcript.text or ""}


def _do_tts(text: str, out: str) -> dict:
    global _tts
    from whispertome.cli import prepare_tts_model
    import soundfile as sf

    if _tts is None:
        _tts = prepare_tts_model(_config, allow_non_npu=False)
    result = _tts.synthesize(text)
    out_path = Path(out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    sf.write(str(out_path), result.speech.samples, result.speech.sample_rate)
    return {"ok": True, "out": str(out_path)}


def _do_voice_status() -> dict:
    from whispertome.models.downloader import missing_groups

    missing = [{"name": g.name, "mb": g.approx_mb} for g in missing_groups(_config)]
    return {"ok": True, "missing": missing}


def _do_download_models() -> dict:
    """Download every missing model group, streaming progress lines between replies."""
    from whispertome.models.downloader import download_group, missing_groups

    for group in missing_groups(_config):
        last = [-1.0]

        def report(label: str, frac: float) -> None:
            # throttle: one line per whole percent, so the pipe stays light
            pct = int(frac * 100)
            if pct > last[0]:
                last[0] = pct
                _reply({"progress": pct, "group": group.name, "label": label})

        download_group(group, on_progress=report)
    still = [g.name for g in missing_groups(_config)]
    if still:
        return {"ok": False, "error": f"still missing after download: {', '.join(still)}"}
    return {"ok": True, "downloaded": True}


def main() -> int:
    _reply({"ok": True, "ready": True})
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
            op = req.get("op")
            if op == "quit":
                _reply({"ok": True, "bye": True})
                return 0
            if op == "ping":
                _reply({"ok": True, "ready": True})
            elif op == "stt":
                _reply(_do_stt(req["wav"]))
            elif op == "tts":
                _reply(_do_tts(req["text"], req["out"]))
            else:
                _reply({"ok": False, "error": f"unknown op: {op!r}"})
        except Exception as exc:  # keep serving — one bad request must not kill the session
            _reply({"ok": False, "error": f"{type(exc).__name__}: {exc}"})
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
