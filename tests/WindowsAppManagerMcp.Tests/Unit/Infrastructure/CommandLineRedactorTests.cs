using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// CommandLineRedactor (roadmap M6.6, shared later by M6.8 and M7.5): what it masks in a process's command line, and,
/// just as important, what it leaves alone so that a redacted line is still worth reading. The rules are heuristic; these
/// tables are their specification.
/// </summary>
public class CommandLineRedactorTests
{
    [Theory]
    // switch=value and switch:value, with -, -- and / prefixes
    [InlineData(@"app.exe --password=hunter2", @"app.exe --password=***")]
    [InlineData(@"app.exe -password=hunter2 --verbose", @"app.exe -password=*** --verbose")]
    [InlineData(@"app.exe /password:hunter2", @"app.exe /password:***")]
    [InlineData(@"app.exe --token=abc.def.ghi", @"app.exe --token=***")]
    [InlineData(@"app.exe --api-key=sk-123", @"app.exe --api-key=***")]
    [InlineData(@"app.exe --apikey=sk-123", @"app.exe --apikey=***")]
    [InlineData(@"app.exe --apiKey=sk-123", @"app.exe --apiKey=***")]
    [InlineData(@"app.exe --access-token=t0k", @"app.exe --access-token=***")]
    [InlineData(@"app.exe --accessToken=t0k", @"app.exe --accessToken=***")]
    [InlineData(@"app.exe --client_secret=s3", @"app.exe --client_secret=***")]
    [InlineData(@"app.exe --clientsecret=s3", @"app.exe --clientsecret=***")]
    [InlineData(@"app.exe --auth=user:pw", @"app.exe --auth=***")]
    [InlineData(@"app.exe --credentials=c", @"app.exe --credentials=***")]
    [InlineData(@"app.exe --sig=abc", @"app.exe --sig=***")]
    [InlineData(@"app.exe --pwd=abc", @"app.exe --pwd=***")]
    [InlineData(@"app.exe --passwd=abc", @"app.exe --passwd=***")]
    [InlineData(@"app.exe --PASSWORD=abc", @"app.exe --PASSWORD=***")]
    [InlineData(@"app.exe --wam-test-token=s3cr3t", @"app.exe --wam-test-token=***")]
    // Quoted values and quoted switches keep their quotes, and lose the whole value, spaces included
    [InlineData(@"app.exe --password=""hunter 2"" --verbose", @"app.exe --password=""***"" --verbose")]
    [InlineData(@"app.exe ""--password=hunter 2"" --verbose", @"app.exe ""--password=***"" --verbose")]
    // A switch's value runs to the end of the argument, a ; included: it is not a connection string
    [InlineData(@"app.exe --password=abc;def", @"app.exe --password=***")]
    [InlineData(@"app.exe --password=""a;b"" --verbose", @"app.exe --password=""***"" --verbose")]
    [InlineData(@"app.exe --token=a;b;c", @"app.exe --token=***")]
    [InlineData(@"app.exe -password=a;b=c", @"app.exe -password=***")]
    // The space-separated form
    [InlineData(@"app.exe --password hunter2 --verbose", @"app.exe --password *** --verbose")]
    [InlineData(@"app.exe /token hunter2", @"app.exe /token ***")]
    [InlineData(@"app.exe --password ""hunter 2""", @"app.exe --password ""***""")]
    // Bearer and Basic credentials, and header-like text
    [InlineData(@"curl -H ""Authorization: Bearer abc.def"" https://example.com", @"curl -H ""Authorization: ***"" https://example.com")]
    [InlineData(@"curl -H ""X-Api-Key: k123"" https://example.com", @"curl -H ""X-Api-Key: ***"" https://example.com")]
    [InlineData(@"curl -H ""Cookie: session=abc"" https://example.com", @"curl -H ""Cookie: ***"" https://example.com")]
    [InlineData(@"tool Bearer abc.def", @"tool Bearer ***")]
    [InlineData(@"tool ""Bearer abc.def""", @"tool ""Bearer ***""")]
    [InlineData(@"tool Basic dXNlcjpwYXNz", @"tool Basic ***")]
    [InlineData(@"tool Authorization: Bearer abc", @"tool Authorization: *** ***")]
    // A header given as the value of a switch (wget --header=)
    [InlineData(@"wget --header=""Authorization: token ghp_SECRET"" https://x", @"wget --header=""Authorization: ***"" https://x")]
    [InlineData(@"wget --header=""X-Api-Key: SECRET"" https://x", @"wget --header=""X-Api-Key: ***"" https://x")]
    [InlineData(@"wget --header=""Cookie: session=SECRET"" https://x", @"wget --header=""Cookie: ***"" https://x")]
    [InlineData(@"tool ""/header:Authorization: token abc""", @"tool ""/header:Authorization: ***""")]
    // URL user info: the password, or a token standing alone where the user name goes
    [InlineData(@"git clone https://user:pa55@example.com/repo.git", @"git clone https://user:***@example.com/repo.git")]
    [InlineData(@"git clone https://ghp_abc123@github.com/o/r.git", @"git clone https://***@github.com/o/r.git")]
    [InlineData(@"app.exe --url=http://u:p@host:8080/x", @"app.exe --url=http://u:***@host:8080/x")]
    // An unencoded @ in the password: the user info runs to the last @ before the host, as git and curl read it
    [InlineData(@"git clone https://user:p@ss@host/repo", @"git clone https://user:***@host/repo")]
    // URL query parameters with secret names, including Azure SAS and AWS signed URLs
    [InlineData(@"app.exe https://acct.blob.core.windows.net/c/b?sv=2022&sig=AbC%2Bd&se=2026", @"app.exe https://acct.blob.core.windows.net/c/b?sv=2022&sig=***&se=2026")]
    [InlineData(@"app.exe https://example.com/cb?access_token=abc#frag", @"app.exe https://example.com/cb?access_token=***#frag")]
    [InlineData(@"app.exe ""https://s3.example.com/o?X-Amz-Signature=ff00&X-Amz-Date=1""", @"app.exe ""https://s3.example.com/o?X-Amz-Signature=***&X-Amz-Date=1""")]
    [InlineData(@"app.exe https://maps.example.com/api?key=AIza123&q=oslo", @"app.exe https://maps.example.com/api?key=***&q=oslo")]
    // Connection strings and assignments
    [InlineData(@"app.exe ""Server=db;User Id=sa;Password=hunter 2;Encrypt=true""", @"app.exe ""Server=db;User Id=sa;Password=***;Encrypt=true""")]
    [InlineData(@"app.exe ""Server=db;Pwd=hunter2""", @"app.exe ""Server=db;Pwd=***""")]
    [InlineData(@"app.exe ""DefaultEndpointsProtocol=https;AccountName=a;AccountKey=Zm9v==;EndpointSuffix=core.windows.net""", @"app.exe ""DefaultEndpointsProtocol=https;AccountName=a;AccountKey=***;EndpointSuffix=core.windows.net""")]
    [InlineData(@"env API_TOKEN=abc node app.js", @"env API_TOKEN=*** node app.js")]
    [InlineData(@"msbuild /p:Password=hunter2;Configuration=Release", @"msbuild /p:Password=***;Configuration=Release")]
    // A ; inside a connection string's or property list's value: the value runs on to the next Name= segment
    [InlineData(@"app.exe ""Password=a;b""", @"app.exe ""Password=***""")]
    [InlineData(@"app.exe ""Server=db;Password=a;b;Encrypt=true""", @"app.exe ""Server=db;Password=***;Encrypt=true""")]
    [InlineData(@"app.exe ""Server=db;Pwd=hunter2;""", @"app.exe ""Server=db;Pwd=***;""")]
    [InlineData(@"msbuild /p:Password=a;b;Configuration=Release", @"msbuild /p:Password=***;Configuration=Release")]
    // A connection string as the value of a switch with an ordinary name
    [InlineData(@"app.exe ""--connection=Server=db;Password=a;b;Encrypt=true""", @"app.exe ""--connection=Server=db;Password=***;Encrypt=true""")]
    // mysql-style -pVALUE, for the MySQL and MariaDB tools only
    [InlineData(@"mysql.exe -u root -phunter2 shop", @"mysql.exe -u root -p*** shop")]
    [InlineData(@"""C:\Program Files\MariaDB\bin\mariadb-dump.exe"" -psecret db", @"""C:\Program Files\MariaDB\bin\mariadb-dump.exe"" -p*** db")]
    // -P of the SQL Server tools, apart or attached
    [InlineData(@"sqlcmd -S srv -U sa -P hunter2", @"sqlcmd -S srv -U sa -P ***")]
    [InlineData(@"sqlcmd.exe -S srv -U sa -Phunter2 -d db", @"sqlcmd.exe -S srv -U sa -P*** -d db")]
    [InlineData(@"bcp db.dbo.t out t.dat -S srv -U sa -P ""hun ter2""", @"bcp db.dbo.t out t.dat -S srv -U sa -P ""***""")]
    [InlineData(@"osql -U sa -P pw", @"osql -U sa -P ***")]
    [InlineData(@"""C:\Program Files\Microsoft SQL Server\Tools\Binn\SQLCMD.EXE"" /U sa /P pw", @"""C:\Program Files\Microsoft SQL Server\Tools\Binn\SQLCMD.EXE"" /U sa /P ***")]
    // A user:password pair given to -u or --user (curl)
    [InlineData(@"curl -u alice:pw123 https://example.com", @"curl -u alice:*** https://example.com")]
    [InlineData(@"curl --user=alice:pw123 https://example.com", @"curl --user=alice:*** https://example.com")]
    // Several at once
    [InlineData(@"app.exe --token=a --password b https://u:p@h/?sig=c", @"app.exe --token=*** --password *** https://u:***@h/?sig=***")]
    public void ASecret_IsMasked(string commandLine, string expected)
    {
        var result = CommandLineRedactor.Redact(commandLine);

        result.Text.Should().Be(expected);
        result.Redacted.Should().BeTrue();
    }

    [Theory]
    // Words that merely contain a secret word
    [InlineData(@"app.exe --keyboard-layout=us")]
    [InlineData(@"app.exe --monkey=banana")]
    [InlineData(@"app.exe monkey=banana")]
    [InlineData(@"app.exe --author=Bob")]
    [InlineData(@"app.exe --authority=https://login.example.com")]
    [InlineData(@"app.exe --keyfile=C:\keys\id.pem")]
    [InlineData(@"app.exe --passive")]
    [InlineData(@"app.exe --signal=TERM")]
    [InlineData(@"app.exe --design=compact")]
    [InlineData(@"app.exe --tokenizer=bpe")]
    // Switches that name where a secret is, or how one is kept, rather than the secret
    [InlineData(@"app.exe --key-file=C:\keys\id.pem")]
    [InlineData(@"app.exe --password-file C:\secrets\pw.txt")]
    [InlineData(@"chrome.exe --password-store=basic")]
    [InlineData(@"app.exe --token-path=C:\t.txt")]
    [InlineData(@"app.exe --auth-type=ntlm")]
    [InlineData(@"app.exe --key-id=k1")]
    // A secret switch followed by another switch has no value to mask
    [InlineData(@"app.exe --password --verbose")]
    [InlineData(@"app.exe /token /quiet")]
    // A switch that turns a secret off has no value
    [InlineData(@"app.exe --no-password notes.txt")]
    // -p is a port, or something else, outside the MySQL tools; -p alone makes mysql prompt
    [InlineData(@"ssh.exe -p2222 host")]
    [InlineData(@"docker.exe run -p8080:80 image")]
    [InlineData(@"mysql.exe -u root -p shop")]
    // -P is a password only for the SQL Server tools; sqlcmd's -p prints statistics
    [InlineData(@"app.exe -P 8080")]
    [InlineData(@"sqlcmd -S srv -E -p")]
    [InlineData(@"sqlcmd -S srv -E -P -d db")]
    // A header with an ordinary name, given as a switch's value
    [InlineData(@"wget --header=""Accept: text/html"" https://x")]
    // An @ after the host is not user info
    [InlineData(@"app.exe https://example.com/u/@user")]
    [InlineData(@"app.exe https://example.com/?mail=a@b.c")]
    // Ordinary arguments, paths, URLs and drive letters
    [InlineData(@"""C:\Program Files\App\app.exe"" --profile-directory=""Profile 1"" C:\docs\notes.txt")]
    [InlineData(@"chrome.exe --remote-debugging-port=9222 --user-data-dir=C:\tmp\p https://example.com/path?q=1&lang=en")]
    [InlineData(@"git clone https://github.com/o/r.git")]
    [InlineData(@"curl -u alice https://example.com")]
    [InlineData(@"app.exe --mode Basic")]
    [InlineData(@"app.exe C:\data\file.txt D:")]
    public void AnOrdinaryArgument_IsKept(string commandLine)
    {
        var result = CommandLineRedactor.Redact(commandLine);

        result.Text.Should().Be(commandLine);
        result.Redacted.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notepad.exe")]
    public void ALineWithNothingToMask_IsReturnedAsItIs(string commandLine)
    {
        var result = CommandLineRedactor.Redact(commandLine);

        result.Text.Should().Be(commandLine);
        result.Redacted.Should().BeFalse();
    }

    [Fact]
    public void Null_IsRefused()
    {
        var act = () => CommandLineRedactor.Redact(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AnEscapedQuoteInsideAQuotedValue_IsPartOfTheMaskedValue()
    {
        // The Windows rule: \" is a literal quote, so the value runs on to the closing quote
        var result = CommandLineRedactor.Redact(@"app.exe ""--password=a\""b c"" --verbose");

        result.Text.Should().Be(@"app.exe ""--password=***"" --verbose");
    }

    [Fact]
    public void ACraftedLongArgument_IsRedactedInLinearTime()
    {
        // Each of these took about half a second with backtracking regexes, which rescanned the argument from every
        // start position; a process sets its own command line, up to Windows' 32,767 characters
        string[] arguments =
        [
            string.Concat(Enumerable.Repeat("a.", 16_000)),
            new string('?', 32_000),
            string.Concat(Enumerable.Repeat("a-", 16_000)),
            "\"a" + new string(' ', 32_000) + "b=\"",
            "a://" + string.Concat(Enumerable.Repeat("a://", 8_000)),
        ];
        CommandLineRedactor.Redact("app.exe --password=x https://u:p@h/?sig=y");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var argument in arguments)
        {
            CommandLineRedactor.Redact("app.exe " + argument);
        }

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void TheMask_IsThreeAsterisks()
    {
        CommandLineRedactor.Mask.Should().Be("***");
    }
}
