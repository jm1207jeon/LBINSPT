// 바코드 손상 등급 — ISO/IEC 15415 지표의 간이 근사.
//
// 정식 15415 검증은 교정된 조명/광학계를 요구하므로, 여기서는 디코드된 심볼
// 영역의 이미지 지표(심볼 대비, 모듈화 근사, 결함 근사)로 A~F 등급을 추정한다.
// 손상 라벨 스크리닝용 참고 등급이며 검교정 장비의 성적서를 대체하지 않는다.
using SkiaSharp;

namespace LabelSuite.Core;

public sealed record BarcodeGrade(
    char Letter,             // A~F
    double Score,            // 4.0 ~ 0.0
    double SymbolContrast,   // 0~1
    double Modulation,       // 0~1
    double Defects)          // 0~1 (높을수록 손상)
{
    public string Display => $"{Letter} ({Score:F1})";
}

public static class BarcodeGrader
{
    /// <summary>디코드된 바코드 영역을 분석해 간이 등급을 매긴다.</summary>
    public static BarcodeGrade Grade(SKBitmap image, (int X, int Y, int W, int H) bbox)
    {
        // 여백(quiet zone) 일부 포함해 영역 추출
        var pad = Math.Max(2, Math.Min(bbox.W, bbox.H) / 10);
        var x0 = Math.Max(0, bbox.X - pad);
        var y0 = Math.Max(0, bbox.Y - pad);
        var x1 = Math.Min(image.Width, bbox.X + bbox.W + pad);
        var y1 = Math.Min(image.Height, bbox.Y + bbox.H + pad);
        if (x1 - x0 < 4 || y1 - y0 < 4)
            return new BarcodeGrade('F', 0, 0, 0, 1);

        // 휘도 수집
        var count = (x1 - x0) * (y1 - y0);
        var luma = new byte[count];
        var index = 0;
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var color = image.GetPixel(x, y);
                luma[index++] = (byte)((color.Red * 299 + color.Green * 587
                                        + color.Blue * 114) / 1000);
            }

        // 2%/98% 퍼센타일로 Rmin/Rmax (노이즈 픽셀 배제)
        var histogram = new int[256];
        foreach (var v in luma) histogram[v]++;
        var rMin = Percentile(histogram, count, 0.02);
        var rMax = Percentile(histogram, count, 0.98);
        var symbolContrast = (rMax - rMin) / 255.0;
        if (rMax - rMin < 8)
            return new BarcodeGrade('F', 0, symbolContrast, 0, 1);

        // 전역 문턱으로 어두운/밝은 모듈 분리 → 모듈화 근사:
        // 각 부류가 문턱에서 얼마나 떨어져 있는지 (경계가 흐릴수록 낮음)
        var threshold = (rMin + rMax) / 2.0;
        double darkSum = 0, lightSum = 0;
        long darkN = 0, lightN = 0, nearBoundary = 0;
        var margin = (rMax - rMin) * 0.125;   // 문턱 ±12.5% 구간은 '애매한' 픽셀
        foreach (var v in luma)
        {
            if (v < threshold) { darkSum += v; darkN++; }
            else { lightSum += v; lightN++; }
            if (Math.Abs(v - threshold) < margin) nearBoundary++;
        }
        if (darkN == 0 || lightN == 0)
            return new BarcodeGrade('F', 0, symbolContrast, 0, 1);
        var separation = (lightSum / lightN - darkSum / darkN) / (rMax - rMin);
        var modulation = Math.Clamp(separation, 0, 1);

        // 결함 근사: 문턱 근처의 애매한 픽셀 비율 (얼룩·긁힘·번짐이 많을수록 증가)
        var defects = (double)nearBoundary / count;

        // 지표별 등급 (ISO 15415 문턱값 준용) 후 최저값 채택
        var contrastScore = symbolContrast >= 0.70 ? 4.0
                          : symbolContrast >= 0.55 ? 3.0
                          : symbolContrast >= 0.40 ? 2.0
                          : symbolContrast >= 0.20 ? 1.0 : 0.0;
        var modulationScore = modulation >= 0.50 ? 4.0
                            : modulation >= 0.40 ? 3.0
                            : modulation >= 0.30 ? 2.0
                            : modulation >= 0.20 ? 1.0 : 0.0;
        var defectsScore = defects <= 0.15 ? 4.0
                         : defects <= 0.20 ? 3.0
                         : defects <= 0.25 ? 2.0
                         : defects <= 0.30 ? 1.0 : 0.0;

        var score = Math.Min(contrastScore, Math.Min(modulationScore, defectsScore));
        var letter = score >= 3.5 ? 'A' : score >= 2.5 ? 'B'
                   : score >= 1.5 ? 'C' : score >= 0.5 ? 'D' : 'F';
        return new BarcodeGrade(letter, score, symbolContrast, modulation, defects);
    }

    private static int Percentile(int[] histogram, int total, double p)
    {
        long acc = 0;
        var target = (long)(total * p);
        for (var v = 0; v < 256; v++)
        {
            acc += histogram[v];
            if (acc >= target) return v;
        }
        return 255;
    }
}
