# Code-Plugin schreiben

Eine vollständige Vorlage liegt in `samples/SampleRunnerPlugin`.

## 1. Projekt anlegen

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\FastEnvSetter.Plugin.Sdk\FastEnvSetter.Plugin.Sdk.csproj" />
  </ItemGroup>
</Project>
```

Außerhalb dieses Repos verweist du stattdessen auf die Pakete `FastEnvSetter.Plugin.Sdk` und `FastEnvSetter.Core` aus den Release-Assets.

## 2. Plugin-Klasse schreiben

```csharp
public sealed class MeinPlugin : IFastEnvPlugin
{
    public void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IRunner, MeinRunner>();
}
```

Die Klasse muss öffentlich sein und einen parameterlosen Konstruktor haben.

## 3. Packen

Lege die erzeugte DLL, eigene Abhängigkeiten und `plugin.json` in einen Ordner und packe ihn:

```powershell
dotnet build -c Release
pwsh scripts/pack-plugin.ps1 -Path .\ausgabe -Output mein-plugin.fesplugin
```

In `plugin.json` gehören `"Kind": "code"` und `"EntryAssembly": "MeinPlugin.dll"`. Bereits von der App bereitgestellte Bibliotheken (`FastEnvSetter.Core`, `FastEnvSetter.Plugin.Sdk`, `Wpf.Ui`, `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`) werden immer aus der App geladen.

## 4. Die App umbauen

Plugins werden nach allen eingebauten Modulen registriert. Bei `GetRequiredService` gewinnt die letzte Registrierung, deshalb kannst du jeden Dienst austauschen:

```csharp
// Eigene Seite in der Seitenleiste
public IEnumerable<NavigationEntry> GetNavigation() =>
    [new("Dashboard", "Board24", typeof(DashboardPage), 5)];

public void ConfigureServices(IServiceCollection services)
{
    services.AddSingleton<DashboardPage>();

    // Eigenes Hauptfenster: Die Klasse erbt von Window und implementiert IAppShell.
    services.AddSingleton<IAppShell, MeinFenster>();
}
```

Mit `Order` legst du die Position in der Seitenleiste fest. Sammlungen wie `IRunner` werden ergänzt; um einen eingebauten Runner zu entfernen, entfernst du dessen Registrierung aus `services`.

Nach Aufbau des Dienstanbieters wird `OnStarted(IServiceProvider)` aufgerufen.

## 5. Testen

Installiere die Datei über **Plugins → Aus Datei** und starte neu. Mit `--data C:\Temp\test.json` bleiben deine echten Daten unberührt.
