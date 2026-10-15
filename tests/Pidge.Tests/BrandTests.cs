using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pidge.Services;
using Xunit.Abstractions;

namespace Pidge.Tests;

// In the GreenMail collection so it uses the throwaway test data folder, never the real one.
[Collection("GreenMail")]
public class BrandTests(GreenMailFixture _, ITestOutputHelper output)
{
    [Theory]
    [InlineData("someone@gmail.com", false)]
    [InlineData("someone@hotmail.co.uk", false)]
    [InlineData("someone@icloud.com", false)]
    [InlineData("someone@btinternet.com", false)]
    [InlineData("noreply@github.com", true)]
    [InlineData("news@mail.bbc.co.uk", true)]
    [InlineData("jake@blackcat.wales", true)]
    public void Only_organisations_get_brands(string address, bool organisation) =>
        Assert.Equal(organisation, Domains.IsOrganisation(address));

    [Theory]
    [InlineData("notifications.github.com", "github.com")]
    [InlineData("e.mail.example.co.uk", "example.co.uk")]
    [InlineData("stripe.com", "stripe.com")]
    public void Subdomains_map_to_the_company_domain(string host, string expected) =>
        Assert.Equal(expected, Domains.BaseDomain(host));

    [Fact]
    public void Picks_the_main_colour_of_a_logo_and_ignores_white_and_grey()
    {
        // 64x64: mostly white, a big orange block, a small grey block.
        var px = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            var i = (y * 64 + x) * 4;
            (byte r, byte g, byte b) = x < 40 && y < 40 ? ((byte)240, (byte)120, (byte)20) : x > 50 ? ((byte)128, (byte)128, (byte)128) : ((byte)255, (byte)255, (byte)255);
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }
        var bmp = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, px, 64 * 4);
        var colour = BrandService.DominantColour(bmp);
        output.WriteLine($"picked {colour}");
        Assert.NotNull(colour);
        Assert.Equal(240, colour!.Value.R);
        Assert.Equal(120, colour.Value.G);

        Assert.False(BrandService.IsBrandColour(Colors.White));
        Assert.False(BrandService.IsBrandColour(Color.FromRgb(30, 30, 30)));
        Assert.True(BrandService.IsBrandColour(Color.FromRgb(0x63, 0x5B, 0xFF)));
    }

    [Theory]
    [InlineData("github.com")]
    [InlineData("stripe.com")]
    [InlineData("bbc.co.uk")]
    [InlineData("linkedin.com")]
    public async Task Fetches_real_company_logos(string domain)
    {
        Assert.StartsWith(System.IO.Path.GetTempPath(), Pidge.Data.Db.DataDir);
        var info = await BrandService.FetchAsync(domain, CancellationToken.None);
        output.WriteLine($"{domain}: icon={(info.IconPath != null ? System.IO.Path.GetFileName(info.IconPath) : "none")} colour={info.Color}");
        Assert.NotNull(info.IconPath);
        var icon = new BitmapImage(new Uri(info.IconPath!));
        Assert.True(icon.PixelWidth >= 16);
        if (domain == "stripe.com") Assert.NotNull(info.Color); // purple logo
        if (domain is "github.com" or "bbc.co.uk") Assert.Null(info.Color); // black/white logos get no tint
    }
}
