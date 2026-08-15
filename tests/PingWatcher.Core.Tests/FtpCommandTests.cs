using PingWatcher.Core.Ftp;
using Xunit;

namespace PingWatcher.Core.Tests;

public class FtpCommandTests
{
    [Fact]
    public void The_verb_is_uppercased()
    {
        FtpCommand command = FtpCommand.Parse("user cisco");

        Assert.Equal("USER", command.Verb);
        Assert.Equal("cisco", command.Argument);
    }

    [Fact]
    public void A_verb_without_an_argument_is_fine()
    {
        FtpCommand command = FtpCommand.Parse("PASV\r\n");

        Assert.Equal("PASV", command.Verb);
        Assert.Equal("", command.Argument);
    }

    [Fact]
    public void Only_the_first_space_splits_so_filenames_with_spaces_survive()
    {
        FtpCommand command = FtpCommand.Parse("STOR my config.txt");

        Assert.Equal("STOR", command.Verb);
        Assert.Equal("my config.txt", command.Argument);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void Blank_lines_yield_an_empty_verb(string line)
    {
        Assert.Equal("", FtpCommand.Parse(line).Verb);
    }
}
