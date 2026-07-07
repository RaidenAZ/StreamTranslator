from types import SimpleNamespace

from asr_worker import extract_text


def test_extract_text_reads_message_content_string():
    completion = SimpleNamespace(
        choices=[
            SimpleNamespace(
                message=SimpleNamespace(content="识别文本"),
            )
        ]
    )

    assert extract_text(completion) == "识别文本"


def test_extract_text_joins_text_parts():
    completion = SimpleNamespace(
        choices=[
            SimpleNamespace(
                message=SimpleNamespace(
                    content=[
                        {"type": "text", "text": "hello"},
                        {"type": "text", "text": " world"},
                    ]
                ),
            )
        ]
    )

    assert extract_text(completion) == "hello world"

