using EduEco.AuthE2E;

// End-to-end authentication and authorization checks against the local Docker stack.
//   dotnet run --project EduEco/Tools/EduEco.AuthE2E [-- --identity https://localhost:7013/ --api ... --bff ... --mailpit ...]
// Exit code 0 = every check passed.
Settings settings;
try
{
    settings = Settings.Load(args);
}
catch (InvalidOperationException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 2;
}

using var scenarios = new Scenarios(settings);
return await scenarios.RunAsync();
