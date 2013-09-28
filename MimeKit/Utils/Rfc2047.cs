//
// Rfc2047.cs
//
// Author: Jeffrey Stedfast <jeff@xamarin.com>
//
// Copyright (c) 2012 Jeffrey Stedfast
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.Text;
using System.Collections.Generic;

using MimeKit.Encodings;

namespace MimeKit.Utils {
	/// <summary>
	/// Utility methods for encoding and decoding rfc2047 encoded-word tokens.
	/// </summary>
	public static class Rfc2047
	{
		class Token {
			public ContentEncoding Encoding;
			public string CharsetName;
			public string CultureName;
			public int CodePage;
			public int StartIndex;
			public int Length;
			public bool Is8bit;

			public string CharsetCulture {
				get {
					if (!string.IsNullOrEmpty (CultureName))
						return CharsetName + "*" + CultureName;

					return CharsetName;
				}
			}

			public Token (string charset, string culture, ContentEncoding encoding, int startIndex, int length)
			{
				CodePage = CharsetUtils.GetCodePage (charset);
				CharsetName = charset;
				CultureName = culture;
				Encoding = encoding;

				StartIndex = startIndex;
				Length = length;
			}

			public Token (int startIndex, int length)
			{
				Encoding = ContentEncoding.Default;
				StartIndex = startIndex;
				Length = length;
			}
		}

		static bool IsAscii (byte c)
		{
			return c.IsAscii ();
		}

		static bool IsAtom (byte c)
		{
			return c.IsAtom ();
		}

		static bool IsBbQq (byte c)
		{
			return c == 'B' || c == 'b' || c == 'Q' || c == 'q';
		}

		static bool IsLwsp (byte c)
		{
			return c.IsWhitespace ();
		}

		static unsafe bool TryGetEncodedWordToken (byte* input, byte* word, int length, out Token token)
		{
			token = null;

			if (length < 7)
				return false;

			byte* inend = word + length - 2;
			byte* inptr = word;

			// check if this could even be an encoded-word token
			if (*inptr++ != '=' || *inptr++ != '?' || *inend++ != '?' || *inend++ != '=')
				return false;

			inend -= 2;

			if (*inptr == '?' || *inptr == '*') {
				// this would result in an empty charset
				return false;
			}

			var charset = new StringBuilder ();
			var culture = new StringBuilder ();

			// find the end of the charset name
			while (inptr < inend && *inptr != '?' && *inptr != '*') {
				if (!IsAtom (*inptr))
					return false;

				charset.Append ((char) *inptr);
				inptr++;
			}

			if (*inptr == '*') {
				// we found a language code...
				inptr++;

				// find the end of the language code
				while (inptr < inend && *inptr != '?') {
					if (!IsAtom (*inptr))
						return false;

					culture.Append ((char) *inptr);
					inptr++;
				}
			}

			if (inptr == inend)
				return false;

			// skip over the '?' to get to the encoding
			inptr++;

			ContentEncoding encoding;
			if (*inptr == 'B' || *inptr == 'b') {
				encoding = ContentEncoding.Base64;
			} else if (*inptr == 'Q' || *inptr == 'q') {
				encoding = ContentEncoding.QuotedPrintable;
			} else {
				return false;
			}

			// skip over the encoding
			inptr++;

			if (*inptr != '?' || inptr == inend)
				return false;

			// skip over the '?' to get to the payload
			inptr++;

			int start = (int) (inptr - input);
			int len = (int) (inend - inptr);

			token = new Token (charset.ToString (), culture.ToString (), encoding, start, len);

			return true;
		}

		static unsafe IList<Token> TokenizePhrase (ParserOptions options, byte* inbuf, int startIndex, int length)
		{
			List<Token> tokens = new List<Token> ();
			byte* text, word, inptr = inbuf + startIndex;
			byte* inend = inptr + length;
			bool encoded = false;
			Token token = null;
			Token lwsp = null;
			bool ascii;
			int n;

			while (inptr < inend) {
				text = inptr;
				while (inptr < inend && IsLwsp (*inptr))
					inptr++;

				if (inptr > text)
					lwsp = new Token ((int) (text - inbuf), (int) (inptr - text));
				else
					lwsp = null;

				word = inptr;
				ascii = true;
				if (inptr < inend && IsAtom (*inptr)) {
					if (options.EnableRfc2047Workarounds) {
						// Make an extra effort to detect and separate encoded-word
						// tokens that have been merged with other words.
						bool is_rfc2047 = false;

						if (inptr + 2 < inend && *inptr == '=' && *(inptr + 1) == '?') {
							inptr += 2;

							// skip past the charset (if one is even declared, sigh)
							while (inptr < inend && *inptr != '?') {
								ascii = ascii && IsAscii (*inptr);
								inptr++;
							}

							// sanity check encoding type
							if (inptr + 3 >= inend || *inptr != '?' || !IsBbQq (*(inptr + 1)) || *(inptr + 2) != '?') {
								ascii = true;
								goto non_rfc2047;
							}

							inptr += 3;

							// find the end of the rfc2047 encoded word token
							while (inptr + 2 < inend && !(*inptr == '?' && *(inptr + 1) == '=')) {
								ascii = ascii && IsAscii (*inptr);
								inptr++;
							}

							if (inptr + 2 >= inend) {
								// didn't find an end marker...
								inptr = word + 2;
								ascii = true;

								goto non_rfc2047;
							}

							is_rfc2047 = true;
							inptr += 2;
						}

					non_rfc2047:
						if (!is_rfc2047) {
							// stop if we encounter a possible rfc2047 encoded
							// token even if it's inside another word, sigh.
							while (inptr < inend && IsAtom (*inptr)) {
								if (inptr + 2 < inend && *inptr == '=' && *(inptr + 1) == '?')
									break;
								inptr++;
							}
						}
					} else {
						// encoded-word tokens are atoms
						while (inptr < inend && IsAtom (*inptr))
							inptr++;
					}

					n = (int) (inptr - word);
					if (TryGetEncodedWordToken (inbuf, word, n, out token)) {
						// rfc2047 states that you must ignore all whitespace between
						// encoded-word tokens
						if (!encoded && lwsp != null) {
							// previous token was not encoded, so preserve whitespace
							tokens.Add (lwsp);
						}

						tokens.Add (token);
						encoded = true;
					} else {
						// append the lwsp and atom tokens
						if (lwsp != null)
							tokens.Add (lwsp);

						token = new Token ((int) (word - inbuf), n);
						token.Is8bit = !ascii;
						tokens.Add (token);

						encoded = false;
					}
				} else {
					// append the lwsp token
					if (lwsp != null)
						tokens.Add (lwsp);

					// append the non-atom token
					ascii = true;
					while (inptr < inend && !IsLwsp (*inptr) && !IsAtom (*inptr)) {
						ascii = ascii && IsAscii (*inptr);
						inptr++;
					}

					token = new Token ((int) (word - inbuf), (int) (inptr - word));
					token.Is8bit = !ascii;
					tokens.Add (token);

					encoded = false;
				}
			}

			return tokens;
		}

		static unsafe IList<Token> TokenizeText (ParserOptions options, byte* inbuf, int startIndex, int length)
		{
			List<Token> tokens = new List<Token> ();
			byte* text, word, inptr = inbuf + startIndex;
			byte* inend = inptr + length;
			bool encoded = false;
			Token token = null;
			Token lwsp = null;
			bool ascii;
			int n;

			while (inptr < inend) {
				text = inptr;
				while (inptr < inend && IsLwsp (*inptr))
					inptr++;

				if (inptr > text)
					lwsp = new Token ((int) (text - inbuf), (int) (inptr - text));
				else
					lwsp = null;

				if (inptr < inend) {
					word = inptr;
					ascii = true;

					if (options.EnableRfc2047Workarounds) {
						// Make an extra effort to detect and separate encoded-word
						// tokens that have been merged with other words.
						bool is_rfc2047 = false;

						if (inptr + 2 < inend && *inptr == '=' && *(inptr + 1) == '?') {
							inptr += 2;

							// skip past the charset (if one is even declared, sigh)
							while (inptr < inend && *inptr != '?') {
								ascii = ascii && IsAscii (*inptr);
								inptr++;
							}

							// sanity check encoding type
							if (inptr + 3 >= inend || *inptr != '?' || !IsBbQq (*(inptr + 1)) || *(inptr + 2) != '?') {
								ascii = true;
								goto non_rfc2047;
							}

							inptr += 3;

							// find the end of the rfc2047 encoded word token
							while (inptr + 2 < inend && !(*inptr == '?' && *(inptr + 1) == '=')) {
								ascii = ascii && IsAscii (*inptr);
								inptr++;
							}

							if (inptr + 2 >= inend) {
								// didn't find an end marker...
								inptr = word + 2;
								ascii = true;

								goto non_rfc2047;
							}

							is_rfc2047 = true;
							inptr += 2;
						}

					non_rfc2047:
						if (!is_rfc2047) {
							// stop if we encounter a possible rfc2047 encoded
							// token even if it's inside another word, sigh.
							while (inptr < inend && !IsLwsp (*inptr)) {
								if (inptr + 2 < inend && *inptr == '=' && *(inptr + 1) == '?')
									break;

								ascii = ascii && IsAscii (*inptr);
								inptr++;
							}
						}
					} else {
						// find the end of a run of text...
						while (inptr < inend && !IsLwsp (*inptr)) {
							ascii = ascii && IsAscii (*inptr);
							inptr++;
						}
					}

					n = (int) (inptr - word);
					if (TryGetEncodedWordToken (inbuf, word, n, out token)) {
						// rfc2047 states that you must ignore all whitespace between
						// encoded-word tokens
						if (!encoded && lwsp != null) {
							// previous token was not encoded, so preserve whitespace
							tokens.Add (lwsp);
						}

						tokens.Add (token);
						encoded = true;
					} else {
						// append the lwsp and atom tokens
						if (lwsp != null)
							tokens.Add (lwsp);

						token = new Token ((int) (word - inbuf), n);
						token.Is8bit = !ascii;
						tokens.Add (token);

						encoded = false;
					}
				} else {
					// append the trailing lwsp token
					if (lwsp != null)
						tokens.Add (lwsp);

					break;
				}
			}

			return tokens;
		}

		static unsafe int DecodeToken (Token token, IMimeDecoder decoder, byte* input, byte* output)
		{
			byte* inptr = input + token.StartIndex;

			return decoder.Decode (inptr, token.Length, output);
		}

		static unsafe string DecodeTokens (ParserOptions options, IList<Token> tokens, byte[] input, byte* inbuf, int length)
		{
			StringBuilder decoded = new StringBuilder (length);
			IMimeDecoder qp = new QuotedPrintableDecoder (true);
			IMimeDecoder base64 = new Base64Decoder ();
			byte[] output = new byte[length];
			Token token;
			int len;

			fixed (byte* outbuf = output) {
				for (int i = 0; i < tokens.Count; i++) {
					token = tokens[i];

					if (token.Encoding != ContentEncoding.Default) {
						// In order to work around broken mailers, we need to combine the raw
						// decoded content of runs of identically encoded word tokens before
						// converting to unicode strings.
						ContentEncoding encoding = token.Encoding;
						int codepage = token.CodePage;
						IMimeDecoder decoder;
						int outlen, n;

						// find the end of this run (and measure the buffer length we'll need)
						for (n = i + 1; n < tokens.Count; n++) {
							if (tokens[n].Encoding != encoding || tokens[n].CodePage != codepage)
								break;
						}

						// base64 / quoted-printable decode each of the tokens...
						if (encoding == ContentEncoding.Base64)
							decoder = base64;
						else
							decoder = qp;

						byte* outptr = outbuf;
						outlen = 0;
						do {
							// Note: by not resetting the decoder state each loop, we effectively
							// treat the payloads as one continuous block, thus allowing us to
							// handle cases where a hex-encoded triplet of a quoted-printable
							// encoded payload is split between 2 or more encoded-word tokens.
							len = DecodeToken (tokens[i], decoder, inbuf, outptr);
							outptr += len;
							outlen += len;
							i++;
						} while (i < n);

						decoder.Reset ();
						i--;

						var unicode = CharsetUtils.ConvertToUnicode (options, codepage, output, 0, outlen, out len);
						decoded.Append (unicode, 0, len);
					} else if (token.Is8bit) {
						// *sigh* I hate broken mailers...
						var unicode = CharsetUtils.ConvertToUnicode (options, input, token.StartIndex, token.Length, out len);
						decoded.Append (unicode, 0, len);
					} else {
						// pure 7bit ascii, a breath of fresh air...
						byte* inptr = inbuf + token.StartIndex;
						byte* inend = inptr + token.Length;

						while (inptr < inend)
							decoded.Append ((char) *inptr++);
					}
				}
			}

			return decoded.ToString ();
		}

		internal static string DecodePhrase (ParserOptions options, byte[] phrase, int startIndex, int count, out int codepage)
		{
			codepage = Encoding.UTF8.CodePage;

			if (count == 0)
				return string.Empty;

			unsafe {
				fixed (byte* inbuf = phrase) {
					var tokens = TokenizePhrase (options, inbuf, startIndex, count);

					// collect the charsets used to encode each encoded-word token
					// (and the number of tokens each charset was used in)
					var codepages = new Dictionary<int, int> ();
					foreach (var token in tokens) {
						if (token.CodePage == 0)
							continue;

						if (!codepages.ContainsKey (token.CodePage))
							codepages.Add (token.CodePage, 1);
						else
							codepages[token.CodePage]++;
					}

					int max = 0;
					foreach (var kvp in codepages) {
						if (kvp.Value <= max)
							continue;

						max = Math.Max (kvp.Value, max);
						codepage = kvp.Key;
					}

					return DecodeTokens (options, tokens, phrase, inbuf, count);
				}
			}
		}

		/// <summary>
		/// Decodes the specified number of bytes of the phrase starting
		/// at the specified starting index.
		/// </summary>
		/// <returns>The decoded phrase.</returns>
		/// <param name="options">The parser options to use.</param>
		/// <param name="phrase">The phrase to decode.</param>
		/// <param name="startIndex">The starting index.</param>
		/// <param name="count">The number of bytes to decode.</param>
		public static string DecodePhrase (ParserOptions options, byte[] phrase, int startIndex, int count)
		{
			if (options == null)
				throw new ArgumentNullException ("options");

			if (phrase == null)
				throw new ArgumentNullException ("phrase");

			if (startIndex < 0 || startIndex > phrase.Length)
				throw new ArgumentOutOfRangeException ("startIndex");

			if (count < 0 || startIndex + count > phrase.Length)
				throw new ArgumentOutOfRangeException ("count");

			if (count == 0)
				return string.Empty;

			unsafe {
				fixed (byte* inbuf = phrase) {
					var tokens = TokenizePhrase (options, inbuf, startIndex, count);

					return DecodeTokens (options, tokens, phrase, inbuf, count);
				}
			}
		}

		/// <summary>
		/// Decodes the specified number of bytes of the phrase starting
		/// at the specified starting index.
		/// </summary>
		/// <returns>The decoded phrase.</returns>
		/// <param name="phrase">The phrase to decode.</param>
		/// <param name="startIndex">The starting index.</param>
		/// <param name="count">The number of bytes to decode.</param>
		public static string DecodePhrase (byte[] phrase, int startIndex, int count)
		{
			return DecodePhrase (ParserOptions.Default, phrase, startIndex, count);
		}

		/// <summary>
		/// Decodes the specified phrase.
		/// </summary>
		/// <returns>The decoded phrase.</returns>
		/// <param name="options">The parser options to use.</param>
		/// <param name="phrase">The phrase to decode.</param>
		public static string DecodePhrase (ParserOptions options, byte[] phrase)
		{
			return DecodePhrase (options, phrase, 0, phrase.Length);
		}

		/// <summary>
		/// Decodes the specified phrase.
		/// </summary>
		/// <returns>The decoded phrase.</returns>
		/// <param name="phrase">The phrase to decode.</param>
		public static string DecodePhrase (byte[] phrase)
		{
			return DecodePhrase (phrase, 0, phrase.Length);
		}

		/// <summary>
		/// Decodes the specified number of bytes of unstructured text starting
		/// at the specified starting index.
		/// </summary>
		/// <returns>The decoded text.</returns>
		/// <param name="options">The parser options to use.</param>
		/// <param name="text">The text to decode.</param>
		/// <param name="startIndex">The starting index.</param>
		/// <param name="count">The number of bytes to decode.</param>
		public static string DecodeText (ParserOptions options, byte[] text, int startIndex, int count)
		{
			if (options == null)
				throw new ArgumentNullException ("options");

			if (text == null)
				throw new ArgumentNullException ("text");

			if (startIndex < 0 || startIndex > text.Length)
				throw new ArgumentOutOfRangeException ("startIndex");

			if (count < 0 || startIndex + count > text.Length)
				throw new ArgumentOutOfRangeException ("count");

			if (count == 0)
				return string.Empty;

			unsafe {
				fixed (byte* inbuf = text) {
					var tokens = TokenizeText (options, inbuf, startIndex, count);

					return DecodeTokens (options, tokens, text, inbuf, count);
				}
			}
		}

		/// <summary>
		/// Decodes the specified number of bytes of unstructured text starting
		/// at the specified starting index.
		/// </summary>
		/// <returns>The decoded text.</returns>
		/// <param name="text">The text to decode.</param>
		/// <param name="startIndex">The starting index.</param>
		/// <param name="count">The number of bytes to decode.</param>
		public static string DecodeText (byte[] text, int startIndex, int count)
		{
			return DecodeText (ParserOptions.Default, text, startIndex, count);
		}

		/// <summary>
		/// Decodes the unstructured text.
		/// </summary>
		/// <returns>The decoded text.</returns>
		/// <param name="options">The parser options to use.</param>
		/// <param name="text">The text to decode.</param>
		public static string DecodeText (ParserOptions options, byte[] text)
		{
			return DecodeText (options, text, 0, text.Length);
		}

		/// <summary>
		/// Decodes the unstructured text.
		/// </summary>
		/// <returns>The decoded text.</returns>
		/// <param name="text">The text to decode.</param>
		public static string DecodeText (byte[] text)
		{
			return DecodeText (text, 0, text.Length);
		}

		static byte[] FoldTokens (FormatOptions options, IList<Token> tokens, string field, byte[] input, bool structured)
		{
			StringBuilder output = new StringBuilder (" ");
			int lineLength = field.Length + 2;
			int lwsp = 0, tab = 0;
			Token token;

			for (int i = 0; i < tokens.Count; i++) {
				token = tokens[i];

				if (input[token.StartIndex].IsWhitespace ()) {
					for (int n = token.StartIndex; n < token.StartIndex + token.Length; n++) {
						if (input[n] == (byte) '\r')
							continue;

						lwsp = output.Length;
						if (input[n] == (byte) '\t')
							tab = output.Length;

						output.Append ((char) input[n]);
						if (input[n] == (byte) '\n') {
							lwsp = tab = 0;
							lineLength = 0;
						} else {
							lineLength++;
						}
					}

					if (lineLength == 0 && i + 1 < tokens.Count) {
						output.Append (structured ? '\t' : ' ');
						lineLength = 1;
					}
				} else if (token.Encoding != ContentEncoding.Default) {
					string charset = token.CharsetCulture;

					if (lineLength + token.Length + charset.Length + 7 > options.MaxLineLength) {
						if (tab != 0) {
							// tabs are the perfect breaking opportunity...
							output.Insert (tab, '\n');
							lineLength = (lwsp - tab) + 1;
						} else if (lwsp != 0) {
							// break just before the last lwsp character
							output.Insert (lwsp, '\n');
							lineLength = 1;
						} else if (lineLength > 1) {
							// force a line break...
							output.Append (structured ? "\n\t" : "\n ");
							lineLength = 1;
						}
					}

					// Note: if the encoded-word token is longer than the fold length, oh well...
					// it probably just means that we are folding a header written by a user-agent
					// with a different max line length than ours.

					output.AppendFormat ("=?{0}?{1}?", charset, token.Encoding == ContentEncoding.Base64 ? 'b' : 'q');
					for (int n = token.StartIndex; n < token.StartIndex + token.Length; n++)
						output.Append ((char) input[n]);
					output.Append ("?=");

					lineLength += token.Length + charset.Length + 7;
					lwsp = 0;
					tab = 0;
				} else if (lineLength + token.Length > options.MaxLineLength) {
					if (tab != 0) {
						// tabs are the perfect breaking opportunity...
						output.Insert (tab, '\n');
						lineLength = (lwsp - tab) + 1;
					} else if (lwsp != 0) {
						// break just before the last lwsp character
						output.Insert (lwsp, '\n');
						lineLength = 1;
					} else if (lineLength > 1) {
						// force a line break...
						output.Append (structured ? "\n\t" : "\n ");
						lineLength = 1;
					}

					if (token.Length >= options.MaxLineLength) {
						// the token is longer than the allowable line length,
						// so we'll have to break it apart...
						int half = token.StartIndex + (options.MaxLineLength - lineLength);

						for (int n = token.StartIndex; n < half; n++)
							output.Append ((char) input[n]);

						output.Append ("\n\t");

						for (int n = half; n < token.StartIndex + token.Length; n++)
							output.Append ((char) input[n]);

						lineLength = (token.Length - half) + 1;
					} else {
						for (int n = token.StartIndex; n < token.StartIndex + token.Length; n++)
							output.Append ((char) input[n]);

						lineLength += token.Length;
					}

					lwsp = 0;
					tab = 0;
				} else {
					for (int n = token.StartIndex; n < token.StartIndex + token.Length; n++)
						output.Append ((char) input[n]);

					lineLength += token.Length;
					lwsp = 0;
					tab = 0;
				}
			}

			if (output[output.Length - 1] != '\n')
				output.Append ('\n');

			return Encoding.ASCII.GetBytes (output.ToString ());
		}

		internal static byte[] FoldStructuredHeader (FormatOptions options, string field, byte[] text)
		{
			unsafe {
				fixed (byte* inbuf = text) {
					var tokens = TokenizeText (ParserOptions.Default, inbuf, 0, text.Length);

					return FoldTokens (options, tokens, field, text, true);
				}
			}
		}

		internal static byte[] FoldUnstructuredHeader (FormatOptions options, string field, byte[] text)
		{
			unsafe {
				fixed (byte* inbuf = text) {
					var tokens = TokenizeText (ParserOptions.Default, inbuf, 0, text.Length);

					return FoldTokens (options, tokens, field, text, false);
				}
			}
		}

		static byte[] CharsetConvert (Encoding charset, char[] word, int length, out int converted)
		{
			var encoder = charset.GetEncoder ();
			int count = encoder.GetByteCount (word, 0, length, true);
			byte[] encoded = new byte[count];

			converted = encoder.GetBytes (word, 0, length, encoded, 0, true);

			return encoded;
		}

		static ContentEncoding GetBestContentEncoding (byte[] text, int startIndex, int length)
		{
			int count = 0;

			for (int i = startIndex; i < startIndex + length; i++) {
				if (text[i] > 127)
					count++;
			}

			if ((double) count < (length * 0.17))
				return ContentEncoding.QuotedPrintable;

			return ContentEncoding.Base64;
		}

		static void AppendEncodeWord (StringBuilder str, Encoding charset, string text, int startIndex, int length, QEncodeMode mode)
		{
			char[] chars = new char[length];
			IMimeEncoder encoder;
			byte[] word, encoded;
			char encoding;
			int len;

			text.CopyTo (startIndex, chars, 0, length);

			try {
				word = CharsetConvert (charset, chars, length, out len);
			} catch {
				charset = Encoding.UTF8;
				word = CharsetConvert (charset, chars, length, out len);
			}

			if (GetBestContentEncoding (word, 0, len) == ContentEncoding.Base64) {
				encoder = new Base64Encoder (true);
				encoding = 'b';
			} else {
				encoder = new QEncoder (mode);
				encoding = 'q';
			}

			encoded = new byte[encoder.EstimateOutputLength (len)];
			len = encoder.Flush (word, 0, len, encoded);

			str.AppendFormat ("=?{0}?{1}?", CharsetUtils.GetMimeCharset (charset), encoding);
			for (int i = 0; i < len; i++)
				str.Append ((char) encoded[i]);
			str.Append ("?=");
		}

		static void AppendQuoted (StringBuilder str, string text, int startIndex, int length)
		{
			int lastIndex = startIndex + length;
			char c;

			str.Append ('"');

			for (int i = startIndex; i < lastIndex; i++) {
				c = text[i];

				if (c == '"' || c == '\\')
					str.Append ('\\');

				str.Append (c);
			}

			str.Append ('"');
		}

		enum WordType {
			Atom,
			QuotedString,
			EncodedWord
		}

		class Word {
			public WordType Type;
			public int StartIndex;
			public int Length;
			public int Encoding;
			public int ByteCount;

			public Word (WordType type, int encoding, int start, int end, int count)
			{
				Encoding = encoding;
				StartIndex = start;
				Length = end - start;
				ByteCount = count;
				Type = type;
			}
		}

		static bool IsAtom (char c)
		{
			return ((byte) c).IsAtom ();
		}

		static bool IsBlank (char c)
		{
			return c == ' ' || c == '\t';
		}

		static bool IsCtrl (char c)
		{
			return ((byte) c).IsCtrl ();
		}

		static IList<Word> GetRfc822Words (FormatOptions options, Encoding charset, string text, bool phrase)
		{
			int encoding = 0, count = 0, start = 0;
			var encoder = charset.GetEncoder ();
			var words = new List<Word> ();
			var type = WordType.Atom;
			var chars = new char[2];
			int n, i = 0;
			Word word;
			char c;

			while (i < text.Length) {
				c = text[i++];

				if (c < 256 && IsBlank (c)) {
					if (count > 0) {
						word = new Word (type, encoding, start, i - 1, count);
						words.Add (word);
						count = 0;
					}

					type = WordType.Atom;
					encoding = 0;
					start = i;
				} else {
					if (c < 127) {
						if (IsCtrl (c)) {
							type = WordType.EncodedWord;
							encoding = Math.Max (encoding, 1);
						} else if (phrase && !IsAtom (c)) {
							// phrases can have quoted strings
							if (type == WordType.Atom)
								type = WordType.QuotedString;
						}
						count++;
					} else if (c < 256) {
						type = WordType.EncodedWord;
						encoding = Math.Max (encoding, 1);
						count++;
					} else {
						if (char.IsSurrogatePair (text, i - 1)) {
							chars[1] = text[i++];
							n = 2;
						} else {
							n = 1;
						}

						chars[0] = c;
						count += encoder.GetByteCount (chars, 0, n, true);
						type = WordType.EncodedWord;
						encoding = 2;
					}

					if (count >= options.MaxPreEncodedLength) {
						// Note: if the word is longer than what we can fit on
						// one line, then we need to encode it.
						if (type == WordType.Atom)
							type = WordType.EncodedWord;

						word = new Word (type, encoding, start, i, count);
						words.Add (word);

						// Note: we don't reset 'type' as it needs to be preserved
						// when breaking long words.
						encoding = 0;
						count = 0;
						start = i;
					}
				}
			}

			if (count > 0) {
				word = new Word (type, encoding, start, text.Length, count);
				words.Add (word);
			}

			return words;
		}

		static bool ShouldMergeWords (FormatOptions options, IList<Word> words, Word word, int i)
		{
			Word next = words[i];

			int lwspCount = next.StartIndex - (word.StartIndex + word.Length);
			int length = word.ByteCount + lwspCount + next.ByteCount;

			switch (word.Type) {
			case WordType.Atom:
				if (next.Type == WordType.EncodedWord)
					return false;

				return length < options.MaxLineLength - 8;
			case WordType.QuotedString:
				if (next.Type == WordType.EncodedWord)
					return false;

				return length < options.MaxLineLength - 8;
			case WordType.EncodedWord:
				if (next.Type == WordType.Atom) {
					// whether we merge or not is dependent upon:
					// 1. the number of atoms in a row after 'word'
					// 2. if there is another encoded-word after
					//    the string of atoms.
					bool merge = false;
					int natoms = 0;

					for (int j = i + 1; j < words.Count && natoms < 3; j++) {
						if (words[j].Type != WordType.Atom) {
							merge = true;
							break;
						}

						natoms++;
					}

					// if all the words after the encoded-word are atoms, don't merge
					if (!merge)
						return false;
				}

				// avoid merging with qstrings
				if (next.Type == WordType.QuotedString)
					return false;

				return length < options.MaxPreEncodedLength;
			default:
				return false;
			}
		}

		static IList<Word> Merge (FormatOptions options, IList<Word> words)
		{
			if (words.Count < 2)
				return words;

			var merged = new List<Word> ();
			int lwspCount, length;
			Word word, next;
			bool merge;

			word = words[0];
			merged.Add (word);

			// first pass: merge qstrings with adjacent qstrings and encoded-words with adjacent encoded-words
			for (int i = 1; i < words.Count; i++) {
				next = words[i];

				if (word.Type != WordType.Atom && word.Type == next.Type) {
					lwspCount = next.StartIndex - (word.StartIndex + word.Length);
					length = word.ByteCount + lwspCount + next.ByteCount;

					if (word.Type == WordType.EncodedWord) {
						merge = length < options.MaxPreEncodedLength;
					} else {
						merge = length < options.MaxLineLength - 8;
					}

					if (merge) {
						word.Length = (next.StartIndex + next.Length) - word.StartIndex;
						word.ByteCount = word.ByteCount + lwspCount + next.ByteCount;
						continue;
					}
				}

				merged.Add (next);
				word = next;
			}

			words = merged;
			merged = new List<Word> ();

			word = words[0];
			merged.Add (word);

			// second pass: now merge atoms with the other words
			for (int i = 1; i < words.Count; i++) {
				next = words[i];

				if (ShouldMergeWords (options, words, word, i)) {
					// the resulting word is the max of the 2 types
					lwspCount = next.StartIndex - (word.StartIndex + word.Length);

					word.Type = (WordType) Math.Max ((int) word.Type, (int) next.Type);
					word.Encoding = Math.Max (word.Encoding, next.Encoding);
					word.Length = (next.StartIndex + next.Length) - word.StartIndex;
					word.ByteCount = word.ByteCount + lwspCount + next.ByteCount;
				} else {
					merged.Add (next);
					word = next;
				}
			}

			return merged;
		}

		static byte[] Encode (FormatOptions options, Encoding charset, string text, bool phrase)
		{
			var mode = phrase ? QEncodeMode.Phrase : QEncodeMode.Text;
			var words = Merge (options, GetRfc822Words (options, charset, text, phrase));
			var latin1 = Encoding.GetEncoding (28591);
			var str = new StringBuilder ();
			int start, length;
			Word prev = null;
			byte[] encoded;

			foreach (var word in words) {
				// append the correct number of spaces between words...
				if (prev != null && !(prev.Type == WordType.EncodedWord && word.Type == WordType.EncodedWord)) {
					start = prev.StartIndex + prev.Length;
					length = word.StartIndex - start;
					str.Append (text, start, length);
				}

				switch (word.Type) {
				case WordType.Atom:
					str.Append (text, word.StartIndex, word.Length);
					break;
				case WordType.QuotedString:
					AppendQuoted (str, text, word.StartIndex, word.Length);
					break;
				case WordType.EncodedWord:
					if (prev != null && prev.Type == WordType.EncodedWord) {
						// include the whitespace between these 2 words in the
						// resulting rfc2047 encoded-word.
						start = prev.StartIndex + prev.Length;
						length = (word.StartIndex + word.Length) - start;

						str.Append (' ');
					} else {
						start = word.StartIndex;
						length = word.Length;
					}

					switch (word.Encoding) {
					case 0: // us-ascii
						AppendEncodeWord (str, Encoding.ASCII, text, start, length, mode);
						break;
					case 1: // iso-8859-1
						AppendEncodeWord (str, latin1, text, start, length, mode);
						break;
					default: // custom charset
						AppendEncodeWord (str, charset, text, start, length, mode);
						break;
					}
					break;
				}

				prev = word;
			}

			encoded = new byte[str.Length];
			for (int i = 0; i < str.Length; i++)
				encoded[i] = (byte) str[i];

			return encoded;
		}

		/// <summary>
		/// Encodes the phrase using the specified charset encoding according
		/// to the rules of RFC 2047.
		/// </summary>
		/// <returns>The encoded phrase.</returns>
		/// <param name="options">The formatting options</param>
		/// <param name="charset">The charset encoding.</param>
		/// <param name="phrase">The phrase to encode.</param>
		public static byte[] EncodePhrase (FormatOptions options, Encoding charset, string phrase)
		{
			if (options == null)
				throw new ArgumentNullException ("options");

			if (charset == null)
				throw new ArgumentNullException ("charset");

			if (phrase == null)
				throw new ArgumentNullException ("phrase");

			return Encode (options, charset, phrase, true);
		}

		/// <summary>
		/// Encodes the phrase using the specified charset encoding according
		/// to the rules of RFC 2047.
		/// </summary>
		/// <returns>The encoded phrase.</returns>
		/// <param name="charset">The charset encoding.</param>
		/// <param name="phrase">The phrase to encode.</param>
		public static byte[] EncodePhrase (Encoding charset, string phrase)
		{
			return EncodePhrase (FormatOptions.Default, charset, phrase);
		}

		/// <summary>
		/// Encodes the unstructured text using the specified charset encoding
		/// according to the rules of RFC 2047.
		/// </summary>
		/// <returns>The encoded text.</returns>
		/// <param name="options">The formatting options</param>
		/// <param name="charset">The charset encoding.</param>
		/// <param name="text">The text to encode.</param>
		public static byte[] EncodeText (FormatOptions options, Encoding charset, string text)
		{
			if (options == null)
				throw new ArgumentNullException ("options");

			if (charset == null)
				throw new ArgumentNullException ("charset");

			if (text == null)
				throw new ArgumentNullException ("text");

			return Encode (options, charset, text, false);
		}

		/// <summary>
		/// Encodes the unstructured text using the specified charset encoding
		/// according to the rules of RFC 2047.
		/// </summary>
		/// <returns>The encoded text.</returns>
		/// <param name="charset">The charset encoding.</param>
		/// <param name="text">The text to encode.</param>
		public static byte[] EncodeText (Encoding charset, string text)
		{
			return EncodeText (FormatOptions.Default, charset, text);
		}

		/// <summary>
		/// Quotes the specified text, enclosing it in double-quotes and escaping
		/// any backslashes and double-quotes within.
		/// </summary>
		/// <param name="text">The text to quote.</param>
		public static string Quote (string text)
		{
			if (text == null)
				throw new ArgumentNullException ("text");

			var sb = new StringBuilder ();

			sb.Append ("\"");
			for (int i = 0; i < text.Length; i++) {
				if (text[i] == '\\' || text[i] == '"')
					sb.Append ('\\');
				sb.Append (text[i]);
			}
			sb.Append ("\"");

			return sb.ToString ();
		}

		/// <summary>
		/// Unquotes the specified text, removing any escaped backslashes and
		/// double-quotes within.
		/// </summary>
		/// <param name="text">The text to unquote.</param>
		public static string Unquote (string text)
		{
			int index = text.IndexOfAny (new char[] { '\r', '\n', '\t', '\\', '"' });

			if (index == -1)
				return text;

			StringBuilder builder = new StringBuilder ();
			bool escaped = false;
			bool quoted = false;

			for (int i = 0; i < text.Length; i++) {
				switch (text[i]) {
				case '\r':
				case '\n':
					break;
				case '\t':
					builder.Append (' ');
					break;
				case '\\':
					if (escaped)
						builder.Append ('\\');
					escaped = !escaped;
					break;
				case '"':
					if (escaped) {
						builder.Append ('"');
						escaped = false;
					} else {
						quoted = !quoted;
					}
					break;
				default:
					builder.Append (text[i]);
					break;
				}
			}

			return builder.ToString ();
		}
	}
}
