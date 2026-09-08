namespace LabelSuite.App;

/// <summary>상태바 메시지 3단계 — UDInspect와 동일 규범.
/// 정보=검정 보통 / 경고=갈색 굵게 / 오류=빨강 굵게. 경고·오류는 다음 정보 메시지에
/// 바로 덮이지 않도록 일정 시간 고정된다(MainWindow).</summary>
public enum StatusLevel { Info, Warn, Error }
