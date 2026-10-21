# Windows MSSQL Studio

Windows üzerinde çalışan, MSSQLStudio’nun temel çalışma akışını hedefleyen bağımsız bir WPF MVP’sidir. Özgün macOS projesinin kaynak kodu kopyalanmamıştır.

## Kapsam

- Windows Authentication veya SQL Server kullanıcı/parolası ile bağlantı
- Object Explorer: tablolar ve görünümler
- SQL editörü, seçili SQL’i çalıştırma ve F5 kısayolu
- Sonuç grid’i, satır sayısı ve çalışma süresi
- Mesajlar alanı ve sorguyu durdurma

Özgün depo macOS 27+, Swift 6, AppKit/SwiftUI, TextKit 2, Microsoft ODBC Driver 18 ve SQLite kullanır. Windows MVP’sinde aynı katmanlı kullanım amacı WPF ve Microsoft.Data.SqlClient ile karşılanmıştır. GitHub sayfasında ayrı bir LICENSE dosyası görünmediği için özgün kod yeniden dağıtılmamış, bu proje bağımsız yazılmıştır.

## Çalıştırma

Windows 10/11 ve .NET 10 SDK gerekir:

```powershell
cd WindowsMssqlStudio
dotnet restore
dotnet run
```

Dağıtılabilir Windows klasörü oluşturmak için:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Uygulama SQL Server’a erişebilen makinede çalıştırılmalıdır. MVP varsayılan olarak `Encrypt=True;TrustServerCertificate=True` kullanır; üretim ortamında sertifika doğrulamasını kurum politikanıza göre düzenleyin.

## Kurulum ve güncelleme

İlk kurulum için `Installer\Setup-MKASqlStudio.cmd` dosyasını, çalışma dosyalarını içeren `WindowsMssqlStudio-release.zip` ile aynı klasöre koyup çalıştırın. Kurulum uygulamayı `%LOCALAPPDATA%\MkaSqlStudio` altına açar ve masaüstüne `MKA SQL Studio` kısayolu ekler.

Uygulama içindeki `Güncelle` düğmesi, GitHub’daki `karamuklu/MKASqlStudio` son sürümünün ZIP dosyasını indirir, uygulama kapandıktan sonra dosyaları kurulum klasörüne kopyalar ve uygulamayı yeniden açar. Güncelleme paketinin GitHub Release içine ZIP asset olarak eklenmesi gerekir.

## Sonraki adımlar

Sekmeli sorgu belgeleri, filtre/sıralama, CSV/JSON dışa aktarma, prosedür ve kolon ağacı, ayarlar/profil saklama ve yürütme planı sonraki sürümlere ayrılabilir.
