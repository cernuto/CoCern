using Microsoft.ML.Tokenizers;
using CoCern.Core;

namespace CoCern.Host;

/// <summary>The shared token counter: the o200k vocabulary, loaded once per process on first use and shared by every caller.</summary>
internal sealed class O200kTokenCounter : ITokenCounter
{
    private static readonly Lazy<Tokenizer> tokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"), LazyThreadSafetyMode.ExecutionAndPublication);

    public string Encoding => "o200k_base";

    public int Count(string text) => text.Length == 0 ? 0 : tokenizer.Value.CountTokens(text);

    public int PrefixLength(string text, int maxTokens) => maxTokens <= 0 || text.Length == 0 ? 0 : tokenizer.Value.GetIndexByTokenCount(text, maxTokens, out _, out _);
}
