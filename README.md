# MacLook

Windows 11'e macOS görünümü kazandıran bir masaüstü uygulaması. Windows'un görev çubuğunu gizler; yerine macOS tarzı bir menü çubuğu, alt bar, Başlat menüsü, Denetim Merkezi, Bildirim Merkezi, Launchpad ve Alt+Tab uygulama değiştiricisi koyar. Kapatınca her şey eski hâline döner.

![MacLook Ayarları](docs/ayarlar.png)

## Özellikler

**Menü çubuğu (üstte)**
- Logo menüsü: Bu Bilgisayar Hakkında, Sistem Ayarları, Uyku, Yeniden Başlat, Kapat, MacLook Ayarları
- Öndeki uygulamanın adı ve Dosya / Düzen / Görünüm / Pencere / Yardım menüleri (kısayolları öndeki uygulamaya gönderir)
- Duvar kâğıdına göre otomatik açık / koyu yazı, buzlu cam arka plan

**Alt bar**
- Solda logo (Başlat menüsü), sabitlenmiş ve açık uygulamalar; sağda gizli simgeler (^), gerçek tepsi ikonları, dil, ağ / ses, saat ve masaüstünü göster köşesi
- Açık uygulamanın altında nokta, öndekinin altında çizgi
- Üstünde durunca canlı pencere önizlemeleri
- Sürükle-bırak ile sıralama, yukarı sürükleyip bırakarak kaldırma
- Orta tık: yeni pencere, sağ tık: pencere listesi ve menü
- Ses ikonunun üstünde fare tekerleği ile ses ayarı

**Paneller**
- **Başlat menüsü** (Windows tuşu): arama, Sabitlenenler, Önerilenler (son dosyalar), Tüm Uygulamalar, hesap ve güç menüsü
- **Denetim Merkezi** (Win+A): ağ, Wi-Fi / Bluetooth (varsa), Odak, Gece Işığı, Yansıt, ses ve parlaklık kaydırıcıları, Şimdi Çalıyor
- **Bildirim Merkezi** (saate tıkla): büyük saat, takvim ve Windows bildirimleri
- **Launchpad**: tüm uygulamalar tam ekran, sayfa sayfa
- **Alt+Tab**: macOS uygulama değiştiricisi (Q: uygulamadan çık, Esc: vazgeç)

Wallpaper Engine kullanılıyorsa bulanık arka planlar onun seçili duvar kâğıdından alınır.

## Gereksinimler

- Windows 11 (Windows 10 2004 ve üzeri çoğunlukla çalışır)
- Yayınlanan sürüm kendi içinde .NET'i taşır, ayrıca kurulum gerekmez

## Kurulum

1. [Releases](../../releases) sayfasından son sürümün `.zip` dosyasını indirin.
2. Bir klasöre çıkarın ve `MacLook.exe`'yi çalıştırın.
3. Bilgisayar açılınca başlaması için: logo menüsü → **MacLook Ayarları** → *Bilgisayar açılınca MacLook'u başlat*.

**Kapatmak:** logo menüsü → **MacLook'tan Çık**. Windows görev çubuğu geri gelir.

Görev çubuğu bir şekilde gizli kalırsa:

```
MacLook.exe --restore-taskbar
```

## Kısayollar

| Kısayol | İşlev |
| --- | --- |
| Windows tuşu | Başlat menüsü |
| Win+A | Denetim Merkezi |
| Alt+Tab | Uygulama değiştirici |
| Win+D / sağ alt köşe | Masaüstünü göster |
| Ctrl+Esc | Windows'un kendi Başlat menüsü |

Windows tuşu ve Alt+Tab davranışı MacLook Ayarları'ndan kapatılabilir.

## Derleme

[.NET 10 SDK](https://dotnet.microsoft.com/download) gerekir.

```
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Komut satırı seçenekleri:

| Seçenek | İşlev |
| --- | --- |
| `--exit` | Çalışan MacLook'u kapatır |
| `--restore-taskbar` | Windows görev çubuğunu geri getirir |
| `--snapshot <klasör>` | Panelleri ekranda göstermeden PNG olarak çizer (tasarım kontrolü için) |

Ayarlar `%APPDATA%\MacLook\config.json` dosyasında tutulur.

## Bilinen sınırlamalar

- Yalnızca ana monitörde çalışır.
- Pencere başlık çubukları Windows tarzında kalır.
- Yönetici olarak çalışan bir pencere öndeyken Windows tuşu ve Alt+Tab yakalanamaz.

## Teşekkürler

- [ManagedShell](https://github.com/cairoshell/ManagedShell): sistem tepsisi ikonları

---

Apple, macOS ve Apple logosu Apple Inc.'in ticari markalarıdır. Bu proje Apple ile bağlantılı değildir; yalnızca görünüm benzetmesi yapan bağımsız bir uygulamadır.
