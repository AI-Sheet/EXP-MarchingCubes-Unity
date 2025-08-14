// --- START OF FILE CaveGen.cs (ПОЛНАЯ ЗАМЕНА) ---

using Unity.Burst;
using Unity.Mathematics;
using System.Runtime.CompilerServices;
using OpenSimplex2V; // Убедитесь, что это пространство имен правильное

[BurstCompile]
public static class CaveGenerator
{
    // --- Обёртки FBM остаются без изменений ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float FBM(long seed, float x, float y, float z, int octaves, float frequency, float* gradientPtr)
    {
        float total = 0;
        float amplitude = 1.0f;
        float freq = frequency;
        const float lacunarity = 2.0f;
        const float persistence = 0.5f;

        for (int i = 0; i < octaves; i++)
        {
            total += OpenSimplex2V.OpenSimplex2V.Noise3_Fallback(seed, x * freq, y * freq, z * freq, gradientPtr) * amplitude;
            freq *= lacunarity;
            amplitude *= persistence;
        }
        return total;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float FBM2D(long seed, float x, float z, int octaves, float frequency, float* gradientPtr)
    {
        // Для 2D-шума просто используем 3D-версию с фиксированной одной координатой.
        return FBM(seed, x, 0, z, octaves, frequency, gradientPtr);
    }

    [BurstCompile]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe float CalculateSDFAtPoint(
        float px, float py, float pz,
        in CaveGenParams settings,
        float* gradientPtr)
    {
        // 1. Генерация высоты рельефа с помощью 2D-шума.
        float terrainHeight = FBM2D(settings.seed, px, pz, 4, settings.surfaceNoiseFrequency, gradientPtr) 
                              * settings.surfaceNoiseAmplitude;
        float groundSdf = py - terrainHeight;

        // 2. Глобальный шум для формирования крупных пещер (макро-уровень).
        float macroFrequency = settings.caveShapeFrequency * 0.1f;
        float macroNoise = FBM(settings.seed, px, py * settings.verticalityFactor, pz, 2, macroFrequency, gradientPtr);
        
        // 3. Детализирующий шум для создания туннелей (микро-уровень).
        float microNoise = FBM(settings.seed, px, py * settings.verticalityFactor, pz, 4, settings.caveShapeFrequency, gradientPtr);
        float microSdf = microNoise - settings.tunnelThickness;

        // 4. Модуляция толщины туннелей с помощью макро-шума.
        // Это убирает артефакты от abs() и создает более естественные широкие пещеры и узкие проходы.
        float tunnelModulation = macroNoise * (settings.tunnelThickness * 0.75f); // Макро-шум изменяет толщину в пределах 75% от базовой
        float finalCaveSdf = microSdf - tunnelModulation;

        // 5. Уменьшение количества дыр на поверхности для целостности ландшафта.
        // Мы плавно переходим от пещерного SDF к сплошной породе по мере приближения к поверхности.
        float surfaceDistance = -groundSdf; // Расстояние до поверхности (положительное над землей, отрицательное под)
        float integrityT = math.saturate(surfaceDistance / settings.surfaceIntegrityDepth);
        finalCaveSdf = math.lerp(finalCaveSdf, 1.0f, 1.0f - integrityT); // 1.0f = сплошная порода

        // 6. Финальное объединение рельефа и пещер.
        return math.max(finalCaveSdf, groundSdf);
    }
}