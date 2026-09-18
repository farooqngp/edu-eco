using EduEco.Identity.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddEduEcoIdentityServer();

var app = builder.Build();

app.UseEduEcoIdentityServer();

await app.RunAsync();
