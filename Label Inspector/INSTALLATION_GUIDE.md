# Label Inspector 설치 가이드 (Python 환경)

다른 컴퓨터에서 Label Inspector 프로그램을 Python으로 실행하기 위한 완전한 설치 가이드입니다.

## 1. 시스템 요구사항

### 운영체제
- Windows 10/11 (64-bit)
- macOS 10.14 이상
- Ubuntu 18.04 이상

### 하드웨어
- RAM: 최소 8GB (권장 16GB)
- 저장공간: 최소 5GB 여유공간
- 인터넷 연결 (AWS 서비스 사용)

## 2. Python 설치

### Windows
1. [Python 공식 웹사이트](https://www.python.org/downloads/)에서 Python 3.8 이상 다운로드
2. 설치 시 **"Add Python to PATH"** 체크박스 반드시 선택
3. 설치 완료 후 명령 프롬프트에서 확인:
```cmd
python --version
pip --version
```

## 3. 프로그램 파일 복사

Label Inspector 프로젝트 폴더 전체를 새 컴퓨터로 복사합니다:
```
Label Inspector/
├── main.py
├── requirements.txt
├── core/
├── gui/
└── (기타 파일들)
```

## 4. Python 가상환경 설정 (권장)

```bash
# 프로젝트 폴더로 이동
cd "Label Inspector"

# 가상환경 생성
python -m venv label_inspector_env

# 가상환경 활성화
# Windows:
label_inspector_env\Scripts\activate

## 5. 필수 라이브러리 설치

### 핵심 라이브러리만 설치 (권장)
```bash
pip install PyQt5==5.15.11
pip install boto3==1.35.82
pip install opencv-python==4.12.0.88
pip install pyzbar==0.1.9
pip install PyMuPDF==1.26.4
pip install pdf2image==1.17.0
pip install pillow==10.4.0
pip install pandas==2.2.3
pip install openpyxl==3.1.5
pip install numpy==2.2.3
```

### 또는 전체 의존성 설치
```bash
pip install -r requirements.txt
```

## 6. AWS 설정 및 자격증명 구성

### AWS CLI 설치
```bash
# Windows (PowerShell 관리자 권한)
msiexec.exe /i https://awscli.amazonaws.com/AWSCLIV2.msi


### AWS 자격증명 설정
```bash
aws configure
```

다음 정보를 입력하세요:
- **AWS Access Key ID**: [AWS 계정의 액세스 키]
- **AWS Secret Access Key**: [AWS 계정의 시크릿 키]
- **Default region name**: `ap-northeast-2` (서울 리전)
- **Default output format**: `json`

### AWS 자격증명 확인
```bash
aws sts get-caller-identity
```

### 대안: 환경변수로 설정
AWS CLI 설치 없이 환경변수로도 설정 가능:

**Windows (명령 프롬프트):**
```cmd
set AWS_ACCESS_KEY_ID=your_access_key_here
set AWS_SECRET_ACCESS_KEY=your_secret_key_here
set AWS_DEFAULT_REGION=ap-northeast-2
```

**Windows (PowerShell):**
```powershell
$env:AWS_ACCESS_KEY_ID="your_access_key_here"
$env:AWS_SECRET_ACCESS_KEY="your_secret_key_here"
$env:AWS_DEFAULT_REGION="ap-northeast-2"
```

**macOS/Linux:**
```bash
export AWS_ACCESS_KEY_ID=your_access_key_here
export AWS_SECRET_ACCESS_KEY=your_secret_key_here
export AWS_DEFAULT_REGION=ap-northeast-2
```

## 7. 추가 시스템 구성요소 설치

### Windows - Visual C++ Redistributable
PyQt5 실행을 위해 필요:
- [Microsoft Visual C++ 2015-2022 Redistributable](https://aka.ms/vs/17/release/vc_redist.x64.exe) 다운로드 및 설치

### PDF 처리를 위한 Poppler 설치

**Windows:**
1. [Poppler for Windows](http://blog.alivate.com.au/poppler-windows/) 다운로드
2. 압축 해제 후 `bin` 폴더를 시스템 PATH에 추가


### 바코드 인식을 위한 ZBar 설치

**Windows:**
- pyzbar 설치 시 자동으로 포함됨


## 8. 프로그램 실행

```bash
# 가상환경 활성화 (설정한 경우)
# Windows:
label_inspector_env\Scripts\activate

# 프로그램 실행
python main.py

## 9. 문제 해결

### 일반적인 오류와 해결방법

**1. "ModuleNotFoundError: No module named 'PyQt5'"**
```bash
pip install PyQt5==5.15.11
```

**2. "botocore.exceptions.NoCredentialsError"**
- AWS 자격증명이 설정되지 않음
- 위의 "6. AWS 설정" 단계를 다시 수행

**3. "ImportError: No module named 'cv2'"**
```bash
pip install opencv-python==4.12.0.88
```

**4. PDF 변환 오류**
- Poppler가 설치되지 않음
- 위의 "Poppler 설치" 단계 수행

**5. 바코드 인식 오류**
```bash
pip install pyzbar==0.1.9
```

### 권한 오류 (Windows)
관리자 권한으로 명령 프롬프트 실행 후 설치

### 네트워크 오류
```bash
pip install --trusted-host pypi.org --trusted-host pypi.python.org --trusted-host files.pythonhosted.org [패키지명]
```

## 10. 성능 최적화 팁

1. **가상환경 사용**: 프로젝트별 의존성 격리
2. **SSD 사용**: 이미지 처리 속도 향상
3. **충분한 RAM**: 대용량 PDF 처리 시 필요
4. **안정적인 인터넷**: AWS Textract API 호출용

## 11. 보안 고려사항

1. **AWS 자격증명 보안**:
   - 액세스 키를 코드에 하드코딩하지 마세요
   - IAM 사용자에게 최소 권한만 부여
   - 정기적으로 액세스 키 교체

2. **방화벽 설정**:
   - AWS 서비스 접근을 위한 HTTPS(443) 포트 허용

## 12. 지원 및 문의

설치 중 문제가 발생하면:
1. 오류 메시지 전체를 복사
2. 운영체제 및 Python 버전 확인
3. 설치 단계별 로그 확인

---

**참고**: 이 가이드는 Label Inspector v1.0 기준으로 작성되었습니다.
