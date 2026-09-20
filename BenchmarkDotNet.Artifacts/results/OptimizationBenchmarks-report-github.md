```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i7-13700KF 3.40GHz, 1 CPU, 24 logical and 16 physical cores
.NET SDK 10.0.301
  [Host]   : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                 | Observations | Mean         | Error        | StdDev      | Gen0   | Gen1   | Allocated |
|----------------------- |------------- |-------------:|-------------:|------------:|-------:|-------:|----------:|
| **EffectModelUpdate**      | **50**           |     **958.2 ns** |    **263.19 ns** |    **14.43 ns** | **0.0973** |      **-** |    **1544 B** |
| JacobianEstimation     | 50           |  56,501.2 ns |  4,628.12 ns |   253.68 ns | 1.0986 |      - |   17696 B |
| PredictionFromJacobian | 50           |     625.9 ns |    139.01 ns |     7.62 ns | 0.0372 |      - |     584 B |
| **EffectModelUpdate**      | **500**          |   **7,256.8 ns** |    **455.57 ns** |    **24.97 ns** | **0.5569** | **0.0076** |    **8784 B** |
| JacobianEstimation     | 500          | 595,112.3 ns | 61,293.27 ns | 3,359.69 ns | 2.9297 |      - |   60896 B |
| PredictionFromJacobian | 500          |     622.5 ns |     71.36 ns |     3.91 ns | 0.0372 |      - |     584 B |
