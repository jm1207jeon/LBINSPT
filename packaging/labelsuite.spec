# -*- mode: python ; coding: utf-8 -*-
"""LabelSuite PyInstaller spec (Windows onedir 빌드).

onedir을 쓰는 이유: PySide6+OpenCV+PyMuPDF 조합은 onefile로 만들면
실행마다 임시 폴더 압축 해제로 기동이 느려지고 백신 오탐이 잦다.
배포는 dist/LabelSuite 폴더를 zip으로 묶어 전달한다.

빌드: packaging/build_win.bat 실행 (또는 pyinstaller packaging/labelsuite.spec)
쓰기 상태(설정/이력/캐시)는 전부 %APPDATA%/LabelSuite — 설치 폴더에 쓰지 않는다.
"""

import os

from PyInstaller.utils.hooks import collect_data_files, collect_submodules

ROOT = os.path.abspath(os.path.join(SPECPATH, ".."))

datas = [
    (os.path.join(ROOT, "labelsuite", "resources"), os.path.join("labelsuite", "resources")),
]
# botocore는 서비스 모델 JSON을 런타임에 읽는다 — 누락 시 Textract 호출 불가
datas += collect_data_files("botocore")

hiddenimports = collect_submodules("zxingcpp")

a = Analysis(
    [os.path.join(ROOT, "labelsuite", "__main__.py")],
    pathex=[ROOT],
    binaries=[],
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    runtime_hooks=[],
    excludes=[
        "PyQt5", "PyQt6", "tkinter",
        "PySide6.QtWebEngineCore", "PySide6.QtWebEngineWidgets",
        "PySide6.Qt3DCore", "PySide6.Qt3DRender", "PySide6.QtCharts",
        "PySide6.QtDataVisualization", "PySide6.QtMultimedia",
        "matplotlib", "IPython", "notebook",
    ],
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="LabelSuite",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=False,          # GUI 앱 — 콘솔 창 없음
    icon=None,              # TODO: .ico 파일이 준비되면 지정
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    name="LabelSuite",
)
