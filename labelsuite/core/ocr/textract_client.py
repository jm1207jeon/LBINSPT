"""AWS Textract 클라이언트 — Qt 비의존, 목킹 가능.

레거시 결함 대응:
- boto3.client() 생성 성공만으로 aws_available=True → validate_credentials()가
  sts.get_caller_identity()로 실제 자격증명을 확인한다.
- 실패 시 빈 리스트 반환("결과 0건"으로 위장) → OcrError를 던져 UI가 표시한다.
- bbox를 표시용 이미지 크기 추정치(기본 1000)로 환산 → 원본 픽셀 크기로 정확 환산.
- 계정 ID/버킷 ARN 하드코딩 제거 (미사용 정보 노출이었음).
"""

from __future__ import annotations

import io
import re
from dataclasses import dataclass

import numpy as np
from PIL import Image

MAX_DIMENSION = 2000       # Textract 전송 전 최대 변 길이 (레거시와 동일)
MIN_PORTRAIT = (750, 1000)
MIN_LANDSCAPE = (600, 600)
JPEG_QUALITY = 85

# 단독 비-GTIN GS1 AI 조각은 텍스트 검사에서 제외 (레거시 _apply_filters)
_NON_GTIN_AI_RE = re.compile(r"\((17|10|240|30|21)\)")


class OcrError(RuntimeError):
    """사용자에게 보여줄 메시지를 담은 OCR 실패."""


def friendly_credential_error(exc: Exception) -> str:
    """SDK 원문 오류를 초보자가 조치할 수 있는 한국어 안내로 변환한다."""
    import os

    message = str(exc)
    name = type(exc).__name__
    credentials_path = os.path.join(os.path.expanduser("~"), ".aws", "credentials")

    not_configured = (
        "EC2 Instance Metadata" in message
        or "Unable to get IAM security credentials" in message
        or "Unable to locate credentials" in message
        or name in ("NoCredentialsError", "CredentialRetrievalError",
                    "ProfileNotFound"))
    if name == "ProfileNotFound":
        return (f"프로필을 찾을 수 없습니다: {message}\n"
                "설정의 프로필 칸을 비우거나(기본 자격증명 사용) 이름을 확인하세요.")
    if not_configured:
        if os.path.exists(credentials_path):
            return ("자격증명을 읽지 못했습니다.\n"
                    f"credentials 파일은 존재합니다: {credentials_path}\n다음을 확인하세요:\n"
                    "1) 파일명이 credentials.txt가 아닌지 (확장자 없어야 함)\n"
                    "2) 첫 줄이 [default] 인지\n"
                    "3) 메모장 저장 인코딩이 'UTF-8' (BOM 아님) 인지\n"
                    "4) 수정 후 프로그램을 완전히 껐다 다시 실행했는지")
        return (f"자격증명이 설정되지 않았습니다.\n{credentials_path} 파일이 없습니다.\n"
                "aws configure를 실행하거나 해당 위치에 파일을 만드세요. (README 참고)")
    if "InvalidClientTokenId" in message or "security token included" in message:
        return ("액세스 키가 잘못되었습니다 (오타 또는 비활성화된 키). "
                "AWS IAM에서 키 상태를 확인하세요.")
    if "SignatureDoesNotMatch" in message:
        return ("Secret Key가 잘못되었습니다 (복사 시 일부 누락됐을 수 있음). "
                "credentials 파일의 aws_secret_access_key를 다시 확인하세요.")
    if (name in ("EndpointConnectionError", "ConnectTimeoutError", "ReadTimeoutError")
            or "timed out" in message.lower()):
        return "AWS에 연결할 수 없습니다. 인터넷/방화벽/프록시를 확인하세요."
    return f"인증 확인 실패: {message}"


@dataclass(frozen=True)
class OcrWord:
    text: str
    bbox: tuple[int, int, int, int]   # 원본 이미지 픽셀 (x, y, w, h)
    confidence: int


@dataclass
class CredentialStatus:
    ok: bool
    identity_arn: str | None = None
    error: str | None = None


class TextractClient:
    def __init__(self, region: str = "ap-northeast-2", profile: str | None = None):
        self.region = region
        self.profile = profile or None
        self._client = None

    def _session(self):
        import boto3

        if self.profile:
            return boto3.Session(profile_name=self.profile, region_name=self.region)
        return boto3.Session(region_name=self.region)

    def validate_credentials(self, timeout_seconds: float = 5.0) -> CredentialStatus:
        """sts get_caller_identity로 자격증명을 실제 검증한다."""
        try:
            from botocore.config import Config

            config = Config(connect_timeout=timeout_seconds, read_timeout=timeout_seconds,
                            retries={"max_attempts": 1})
            sts = self._session().client("sts", config=config)
            identity = sts.get_caller_identity()
            return CredentialStatus(ok=True, identity_arn=identity.get("Arn"))
        except Exception as exc:
            return CredentialStatus(ok=False, error=friendly_credential_error(exc))

    def _textract(self):
        if self._client is None:
            self._client = self._session().client("textract")
        return self._client

    @staticmethod
    def _encode_jpeg(image_rgb: np.ndarray) -> bytes:
        """Textract 권장 크기로 리사이즈 후 JPEG 인코딩 (레거시 최적화 로직 이식)."""
        pil = Image.fromarray(image_rgb)
        width, height = pil.size
        if width > MAX_DIMENSION or height > MAX_DIMENSION:
            scale = min(MAX_DIMENSION / width, MAX_DIMENSION / height)
            pil = pil.resize((int(width * scale), int(height * scale)),
                             Image.Resampling.LANCZOS)
        elif width < MIN_PORTRAIT[0] or height < MIN_PORTRAIT[1]:
            if height > width:
                scale = max(MIN_PORTRAIT[0] / width, MIN_PORTRAIT[1] / height)
            else:
                scale = max(MIN_LANDSCAPE[0] / width, MIN_LANDSCAPE[1] / height)
            pil = pil.resize((int(width * scale), int(height * scale)),
                             Image.Resampling.LANCZOS)
        buffer = io.BytesIO()
        if pil.mode != "RGB":
            pil = pil.convert("RGB")
        pil.save(buffer, format="JPEG", quality=JPEG_QUALITY, optimize=True)
        return buffer.getvalue()

    def detect_words(self, image_rgb: np.ndarray) -> list[OcrWord]:
        """Textract detect_document_text 호출 → 원본 픽셀 좌표의 WORD 목록.

        실패는 절대 빈 리스트로 위장하지 않고 OcrError로 던진다.
        """
        if image_rgb is None or image_rgb.size == 0:
            raise OcrError("OCR할 이미지가 없습니다.")
        height, width = image_rgb.shape[:2]
        payload = self._encode_jpeg(image_rgb)

        try:
            response = self._textract().detect_document_text(Document={"Bytes": payload})
        except Exception as exc:
            name = type(exc).__name__
            if "Credential" in name or "NoCredentials" in name:
                raise OcrError(
                    "AWS 자격증명이 없습니다. 설정에서 프로필/자격증명을 확인하세요.") from exc
            raise OcrError(f"AWS Textract 호출 실패: {exc}") from exc

        words: list[OcrWord] = []
        for block in response.get("Blocks", []):
            if block.get("BlockType") != "WORD" or "Text" not in block:
                continue
            text = block["Text"].strip()
            if not text:
                continue
            text = (text.replace("—", "-").replace("–", "-")
                        .replace("\xa9", "(C)"))
            # 단독 비-GTIN AI 조각 제외 — (01) 포함 문자열은 GTIN 검사용으로 유지
            if "(01)" not in text and _NON_GTIN_AI_RE.search(text):
                continue
            bbox = block.get("Geometry", {}).get("BoundingBox")
            if not bbox:
                continue
            words.append(OcrWord(
                text=text,
                bbox=(int(bbox["Left"] * width), int(bbox["Top"] * height),
                      int(bbox["Width"] * width), int(bbox["Height"] * height)),
                confidence=int(block.get("Confidence", 0.0)),
            ))
        return words
