SyntheticRegression.Run();
if (args.Contains("--real"))
    OnimushaRegression.Run(args.FirstOrDefault(a => !a.StartsWith("--")) ?? @"E:\Steam\steamapps\common\OnimushaWotS");
