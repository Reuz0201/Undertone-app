using System.Linq;
using System.Text;
using Undertone.Core;

namespace Undertone.Tests;

public class FriendsTests
{
    [Fact]
    public void Code_IsReadableAndNormalized()
    {
        var code = FriendsFeed.NewCode();
        Assert.Matches("^[A-Z2-9]{4}(-[A-Z2-9]{4}){4}$", code);
        Assert.DoesNotContain('0', code);
        Assert.DoesNotContain('O', code);
        Assert.Equal(FriendsFeed.NormalizeCode(code), FriendsFeed.NormalizeCode(" " + code.ToLowerInvariant().Replace("-", " ") + " "));
        Assert.True(FriendsFeed.IsValidCode(code));
        Assert.False(FriendsFeed.IsValidCode("abc"));
    }

    [Fact]
    public void Encryption_RoundTripsAndRejectsOtherCode()
    {
        var key = FriendsFeed.DeriveKey("AAAABBBBCCCCDDDD");
        var other = FriendsFeed.DeriveKey("AAAABBBBCCCCDDDE");
        var data = FriendsFeed.Encrypt(key, Encoding.UTF8.GetBytes("привет"));
        Assert.Equal("привет", Encoding.UTF8.GetString(FriendsFeed.Decrypt(key, data)!));
        Assert.Null(FriendsFeed.Decrypt(other, data));
        Assert.Null(FriendsFeed.Decrypt(key, new byte[5]));
        // одинаковый текст шифруется по-разному (случайный nonce)
        Assert.NotEqual(data, FriendsFeed.Encrypt(key, Encoding.UTF8.GetBytes("привет")));
    }

    [Fact]
    public void Topic_DoesNotRevealCode()
    {
        var topic = FriendsFeed.TopicId("AAAABBBBCCCCDDDD");
        Assert.Equal(32, topic.Length);
        Assert.DoesNotContain("AAAA", topic.ToUpperInvariant());
        Assert.Equal(topic, FriendsFeed.TopicId("AAAABBBBCCCCDDDD"));
    }
}

public class MqttLiteTests
{
    [Theory]
    [InlineData(0, new byte[] { 0x00 })]
    [InlineData(127, new byte[] { 0x7F })]
    [InlineData(128, new byte[] { 0x80, 0x01 })]
    [InlineData(16383, new byte[] { 0xFF, 0x7F })]
    [InlineData(2097151, new byte[] { 0xFF, 0xFF, 0x7F })]
    public void RemainingLength(int length, byte[] expected) =>
        Assert.Equal(expected, MqttLite.EncodeRemainingLength(length));

    [Fact]
    public void Connect_HasProtocolHeaderAndWill()
    {
        var body = MqttLite.EncodeConnect("id", 60, ("t", new byte[] { 9 }, true));
        Assert.Equal(new byte[] { 0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 4 }, body.Take(7));
        Assert.Equal((byte)(0x02 | 0x04 | 0x20), body[7]); // clean session + will + will retain
        Assert.Equal(new byte[] { 0, 60 }, body.Skip(8).Take(2));
    }

    [Fact]
    public void Publish_RoundTrips()
    {
        var body = MqttLite.EncodePublish("a/b", new byte[] { 1, 2, 3 });
        Assert.True(MqttLite.TryDecodePublish(0x31, body, out var topic, out var payload));
        Assert.Equal("a/b", topic);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload);
    }
}
