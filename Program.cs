using TcpProxy;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ProxyManager>();
var app = builder.Build();

var manager = app.Services.GetRequiredService<ProxyManager>();
await manager.LoadAndStartAsync();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/proxies", async () => Results.Ok(await manager.ListAsync()));

app.MapPost("/api/proxies", async (ProxyUpsert dto) =>
{
    var (view, error) = await manager.CreateAsync(dto);
    return error is not null ? Results.BadRequest(new { error }) : Results.Ok(view);
});

app.MapPut("/api/proxies/{id:guid}", async (Guid id, ProxyUpsert dto) =>
{
    var (view, error) = await manager.UpdateAsync(id, dto);
    if (error is not null)
        return Results.BadRequest(new { error });
    return view is null ? Results.NotFound() : Results.Ok(view);
});

app.MapDelete("/api/proxies/{id:guid}", async (Guid id) =>
    await manager.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

app.Run();
