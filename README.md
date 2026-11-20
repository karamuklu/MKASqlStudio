# Windows MSSQL Studio

Windows üzerinde çalışan, MSSQLStudio’nun temel çalışma akışını hedefleyen bağımsız bir WPF MVP’sidir. Özgün macOS projesinin kaynak kodu kopyalanmamıştır.

## Mimari

Bu sürüm, Windows dağıtımı ve mevcut kullanıcı akışını korumak için WPF + .NET 10 üzerinde çalışır. Arayüz katmanı `MainWindow.xaml`, SQL erişimi `Microsoft.Data.SqlClient`, editör `AvalonEdit`, bağlantı profilleri ve çalışma alanı verileri ise `%LOCALAPPDATA%\\MkaSqlStudio` altındaki ayrı saklama sınıfları ile yönetilir.

Ana parçalar:

- `MainWindow.xaml` / `MainWindow.xaml.cs`: ana pencere, sekmeli editör, Object Explorer ve sonuç alanı
- `ConnectionDialog.xaml` / `ConnectionDialog.xaml.cs`: bağlantı profili ve sunucu doğrulama akışı
- `ConnectionProfile.cs`: Windows DPAPI ile kullanıcıya bağlı parola saklama
- `WorkspaceStores.cs`: sorgu oturumu ve sorgu geçmişi
- `Installer`: kurulum, onarım ve güncelleme betikleri

Prompttaki Tauri/React/Monaco/TanStack seçimi bu mevcut WPF ürününden farklı bir yeniden yazım olur. Bu nedenle çalışan Windows istemcisini koruyan aşamalı mimari seçildi; büyük bileşenler (sanal tablo, execution plan ve dışa aktarma) sonraki modüller olarak ayrılabilir.

## Kapsam

- Windows Authentication veya SQL Server kullanıcı/parolası ile bağlantı
- Object Explorer: tablolar ve görünümler
- SQL editörü, seçili SQL’i çalıştırma ve F5 kısayolu
- Sonuç grid’i, satır sayısı ve çalışma süresi
- Mesajlar alanı ve sorguyu durdurma
- `GO` batch ayracı ile çoklu batch çalıştırma
- Birden fazla sonuç kümesini ayrı sonuç sekmelerinde gösterme
- Sorgu geçmişi ve uygulama kapanınca sekmeleri geri yükleme
- Alt durum çubuğunda imleç satır/sütun konumu

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

## Eksiklerin aşamalı planı

1. Sonuç dışa aktarma: CSV/JSON/Excel ve INSERT scripti
2. Editör: formatlama, yorum kısayolları, daha isabetli JOIN önerileri ve gelişmiş seçim davranışı
3. Sonuç alanı: sanal yükleme, kolon sabitleme/gizleme, NULL biçimi ve hücre detay görünümü
4. Tema ve çalışma alanı: koyu tema, panel görünürlükleri, komut paleti ve dil dosyaları
5. SQL Server özellikleri: execution plan, istatistikler ve production güvenlik onayları

## Sonraki adımlar

Sekmeli sorgu belgeleri, filtre/sıralama, CSV/JSON dışa aktarma, prosedür ve kolon ağacı, ayarlar/profil saklama ve yürütme planı sonraki sürümlere ayrılabilir.

