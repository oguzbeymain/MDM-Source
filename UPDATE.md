# MDM / DownloadMuck — Otomatik Güncelleme

## Repolar

| Repo | URL | Amaç |
|------|-----|------|
| Kaynak (private) | https://github.com/oguzbeymain/MDM-Source | Geliştirme kaynak kodu |
| Uygulama (public) | https://github.com/oguzbeymain/MDM-App | Release build paketleri |

## Nasıl çalışır?

1. Uygulama açılışta `MDM-App` reposundaki **en son GitHub Release** bilgisini okur.
2. Release etiketi (`v1.0.1`) mevcut sürümden yeniyse `.zip` (veya `.exe`) asset indirilir.
3. Geçici bir güncelleyici eski süreci kapatıp dosyaları değiştirir ve uygulamayı yeniden başlatır.

> Debug derlemesinde (`bin\Debug`) otomatik güncelleme **atlanır**.

## Yeni sürüm yayınlama

1. `DownloadMuck.csproj` içindeki `Version` değerini artırın (script de yapabilir).
2. PowerShell:

```powershell
cd C:\Users\oguz\source\repos\DownloadMuck\DownloadMuck
.\scripts\Publish-Release.ps1 -Version 1.0.1 -Notes "Kısa değişiklik özeti"
```

3. Script:
   - Release self-contained `win-x86` paket üretir
   - `artifacts\MDM-1.0.1-win-x86.zip` oluşturur
   - `oguzbeymain/MDM-App` üzerinde `v1.0.1` release açar / asset yükler

## Release kuralları

- Tag formatı: `vMAJOR.MINOR.PATCH` (ör. `v1.0.1`)
- Asset: mümkünse **`.zip`** (tercih edilen); yoksa `.exe`
- Zip içinde kökte veya tek alt klasörde `DownloadMuck.exe` olmalı

## Gereksinimler (yayın için)

- [GitHub CLI](https://cli.github.com/) — `gh auth login`
- `MDM-App` reposuna release yazma yetkisi
