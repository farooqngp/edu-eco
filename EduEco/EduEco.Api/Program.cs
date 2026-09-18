using EduEco.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddEduEcoApi();

var app = builder.Build();

app.UseEduEcoApi();

await app.RunAsync();
