using EduEco.Bff.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddEduEcoBff();

var app = builder.Build();

app.UseEduEcoBff();

await app.RunAsync();
