using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace WinWispr.Translation;

/// <summary>
/// The tokenizer of one Opus-MT direction, built from the model's own files: the source SentencePiece unigram
/// model produces piece strings, <c>vocab.json</c> maps pieces to model ids (unknown pieces map to the unknown id),
/// and the end-of-sequence id closes the input. Decoding joins the pieces the way the Hugging Face MarianTokenizer
/// does. The export's <c>tokenizer.json</c> is deliberately not used (QUALIFICATION.md section 3).
/// </summary>
internal sealed class MarianTokenizer
{
    internal const int EosId = 0;
    internal const int UnknownId = 1;
    internal const int PadId = 58100;

    private readonly SentencePieceTokenizer _source;
    private readonly Dictionary<string, int> _vocabulary;
    private readonly string[] _idToPiece;

    internal MarianTokenizer(string directionDirectory)
    {
        using (FileStream source = File.OpenRead(Path.Combine(directionDirectory, "source.spm")))
            _source = SentencePieceTokenizer.Create(source, addBeginningOfSentence: false, addEndOfSentence: false);

        // The target model only decides how pieces are joined and the joining needs no model, but a target file
        // that the library cannot read means a damaged installation, so it is validated here.
        using (FileStream target = File.OpenRead(Path.Combine(directionDirectory, "target.spm")))
            _ = SentencePieceTokenizer.Create(target, addBeginningOfSentence: false, addEndOfSentence: false);

        using FileStream vocabulary = File.OpenRead(Path.Combine(directionDirectory, "vocab.json"));
        _vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabulary)
            ?? throw new InvalidDataException("vocab.json is empty.");
        if (_vocabulary.Count == 0)
            throw new InvalidDataException("vocab.json is empty.");
        _idToPiece = new string[_vocabulary.Values.Max() + 1];
        foreach ((string piece, int id) in _vocabulary)
            _idToPiece[id] = piece;
    }

    /// <summary>The model ids of <paramref name="text"/>, including the final end-of-sequence id.</summary>
    internal int[] Encode(string text)
    {
        if (text.Length == 0)
            return [EosId];

        IReadOnlyList<EncodedToken> tokens = _source.EncodeToTokens(
            text, out _, considerPreTokenization: false, considerNormalization: true);
        var ids = new int[tokens.Count + 1];
        for (int i = 0; i < tokens.Count; i++)
            ids[i] = _vocabulary.TryGetValue(tokens[i].Value, out int id) ? id : UnknownId;
        ids[^1] = EosId;
        return ids;
    }

    /// <summary>The equivalent of <c>MarianTokenizer.decode(ids, skip_special_tokens=True)</c>.</summary>
    internal string Decode(IEnumerable<int> ids)
    {
        var text = new StringBuilder();
        foreach (int id in ids)
        {
            if (id is EosId or UnknownId or PadId)
                continue;
            // An id outside the vocabulary is the unknown token, which is special and dropped.
            if (id < 0 || id >= _idToPiece.Length || _idToPiece[id] is not { } piece)
                continue;
            text.Append(piece);
        }

        return text.ToString().Replace('▁', ' ').Trim();
    }
}
