using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ICSharpCode.SharpZipLib.Zip;
using Raven.Database;
using Raven.Server;
using Raven.Server.Responders;
using Xunit;

namespace Raven.Scenarios
{
	public class Scenario
	{
		private const int testPort = 58080;
		private readonly string file;

		private readonly Regex[] guidFinders = new[]
		{
			new Regex(
				@",""expectedETag"":""(\{{0,1}([0-9a-fA-F]){8}-([0-9a-fA-F]){4}-([0-9a-fA-F]){4}-([0-9a-fA-F]){4}-([0-9a-fA-F]){12}\}{0,1})"",")
			,
			new Regex(
				@"etag"":""(\{{0,1}([0-9a-fA-F]){8}-([0-9a-fA-F]){4}-([0-9a-fA-F]){4}-([0-9a-fA-F]){4}-([0-9a-fA-F]){12}\}{0,1})""",RegexOptions.IgnoreCase)
			,
			new Regex(
				@"Key"":""(\{{0,1}([0-9a-fA-F]){8}-([0-9a-fA-F]){4}-([0-9a-fA-F]){4}-([0-9a-fA-F]){4}-([0-9a-fA-F]){12}\}{0,1})""", RegexOptions.IgnoreCase)
			,
			new Regex(
				@"id"":""(\{{0,1}([0-9a-fA-F]){8}-([0-9a-fA-F]){4}-([0-9a-fA-F]){4}-([0-9a-fA-F]){4}-([0-9a-fA-F]){12}\}{0,1})""")
			,
			new Regex(@"Timestamp"":""\\/Date(\(\d\d\d\d\d\d\d\d\d\d\d\d\d\+\d\d\d\d\))\\/"""),
		};

		private string lastEtag;
		private int responseNumber;

		public Scenario(string file)
		{
			this.file = file;
		}

		public void Execute()
		{
			var tempFileName = Path.GetTempFileName();
			File.Delete(tempFileName);
			try
			{
				RavenDbServer.EnsureCanListenToWhenInNonAdminContext(testPort);
				using (new RavenDbServer(new RavenConfiguration
				{
					DataDirectory = tempFileName,
					Port = testPort,
					AnonymousUserAccessMode = AnonymousUserAccessMode.All,
					ShouldCreateDefaultsWhenBuildingNewDatabaseFromScratch = false
				}))
				{
					using (var zipFile = new ZipFile(file))
					{
						var zipEntries = zipFile.OfType<ZipEntry>()
							.Where(x => x.Name.StartsWith("raw/"))
							.Where(x => Path.GetExtension(x.Name) == ".txt")
							.GroupBy(x => x.Name.Split('_').First())
							.Select(x => new {Request = x.First(), Response = x.Last()})
							.ToArray();

						foreach (var pair in zipEntries)
						{
							TestSingleRequest(
								new StreamReader(zipFile.GetInputStream(pair.Request)).ReadToEnd(),
								zipFile.GetInputStream(pair.Response).ReadData()
								);
						}
					}
				}
			}
			finally
			{
				Directory.Delete(tempFileName, true);
			}
		}


		private void TestSingleRequest(string request, byte[] expectedResponse)
		{
			Tuple<string, NameValueCollection, string> actual;
			var count = 0;
			do
			{
				actual = SendRequest(request);
				count++;
				if (!IsStaleResponse(actual.Item1))
					break;
				Thread.Sleep(100);
			} while (count < 50);
			if (IsStaleResponse(actual.Item1))
				throw new InvalidOperationException("Request remained stale for too long");

			lastEtag = actual.Item2["ETag"];
			responseNumber++;
			CompareResponses(
				expectedResponse,
				actual,
				request);
		}

		private Tuple<string, NameValueCollection, string> SendRequest(string request)
		{
			using (var sr = new StringReader(request))
			{
				var reqParts = sr.ReadLine().Split(' ');
				var uriString = reqParts[1].Replace(":8080/", ":" + testPort + "/");
				var uri = GetUri_WorkaroundForStrangeBug(uriString);
				var req = (HttpWebRequest) WebRequest.Create(uri);
				req.Method = reqParts[0];

				string header;
				while (string.IsNullOrEmpty((header = sr.ReadLine())) == false)
				{
					var headerParts = header.Split(new[] {": "}, 2, StringSplitOptions.None);
					if (
						new[] {"Host", "Content-Length", "User-Agent"}.Any(
							s => s.Equals(headerParts[0], StringComparison.InvariantCultureIgnoreCase)))
						continue;
					if ((headerParts[0] == "If-Match" || headerParts[0] == "If-None-Match") &&
						IsValidETag(headerParts))
						headerParts[1] = lastEtag;
					req.Headers[headerParts[0]] = headerParts[1];
				}

				if (req.Method != "GET")
				{
					using (var requestStream = req.GetRequestStream())
					using (var writer = new StreamWriter(requestStream))
					{
						writer.Write(sr.ReadToEnd());
						writer.Flush();
					}
				}

				var webResponse = GetResponse(req);
				{
					return new Tuple<string, NameValueCollection, string>(
						new StreamReader(webResponse.GetResponseStream()).ReadToEnd(),
						webResponse.Headers,
						"HTTP/" + webResponse.ProtocolVersion + " " + (int) webResponse.StatusCode + " " +
							webResponse.StatusDescription);
				}
			}
		}

		/// <summary>
		/// 	No, I am not insane, working around a framework issue:
		/// 	http://ayende.com/Blog/archive/2010/03/04/is-select-system.uri-broken.aspx
		/// </summary>
		private static Uri GetUri_WorkaroundForStrangeBug(string uriString)
		{
			Uri uri;
			try
			{
				uri = new Uri(uriString);
			}
			catch (Exception)
			{
				uri = new Uri(uriString);
			}
			return uri;
		}

		private static bool IsValidETag(string[] headerParts)
		{
			try
			{
				return new Guid(headerParts[1]) != Guid.Empty;
			}
			catch
			{
				return false;
			}
		}

		private static HttpWebResponse GetResponse(HttpWebRequest req)
		{
			HttpWebResponse webResponse;
			try
			{
				webResponse = (HttpWebResponse) req.GetResponse();
			}
			catch (WebException e)
			{
				webResponse = (HttpWebResponse) e.Response;
			}
			return webResponse;
		}

		private static bool IsStaleResponse(string response)
		{
			return response.Contains("\"IsStale\":true");
		}

		private void CompareResponses(byte[] response, Tuple<string, NameValueCollection, string> actual, string request)
		{
			var responseAsString = HandleChunking(response);
			foreach (var finder in guidFinders)
			{
				var actuals = finder.Matches(actual.Item1);
				var expected = finder.Matches(responseAsString);
				if (actuals.Count != expected.Count)
					continue;

				for (var i = 0; i < actuals.Count; i++)
				{
					var actualMatch = actuals[i];
					var expectedMatch = expected[i];

					var expectedEtag = expectedMatch.Groups[1].Value;
					if (string.IsNullOrEmpty(expectedEtag) == false)
					{
						responseAsString = responseAsString.Replace(expectedEtag, actualMatch.Groups[1].Value);
					}
				}
			}
			var sr = new StringReader(responseAsString);
			var statusLine = sr.ReadLine();
			if (statusLine != actual.Item3)
			{
				throw new InvalidDataException(
					string.Format("Request {0} status differs. Expected {1}, Actual {2}\r\nRequest:\r\n{3}",
					              responseNumber, statusLine, actual.Item3, request));
			}
			string header;
			while (string.IsNullOrEmpty((header = sr.ReadLine())) == false)
			{
				var parts = header.Split(new[] {": "}, 2, StringSplitOptions.None);
				if (parts[0] == "Content-Length")
					continue;
				if (parts[0] == "Date" || parts[0] == "ETag" || parts[0] == "Location")
				{
					Assert.Contains(parts[0], actual.Item2.AllKeys);
					continue;
				}
				if (actual.Item2[parts[0]] != parts[1])
				{
					throw new InvalidDataException(
						string.Format("Request {0} header {1} differs. Expected {2}, Actual {3}\r\nRequest:\r\n{4}",
						              responseNumber, parts[0], parts[1], actual.Item2[parts[0]], request));
				}
			}

			string expectedLine;
			var rr = new StringReader(actual.Item1);
			var line = 0;
			while (string.IsNullOrEmpty((expectedLine = sr.ReadLine())) == false)
			{
				line++;
				var actualLine = rr.ReadLine();
				if (expectedLine != actualLine)
				{
					var firstDiff = FindFirstDiff(expectedLine, actualLine);
					var outputName = Path.GetFileNameWithoutExtension(file) + " request #" + responseNumber + ".txt";
					File.WriteAllText(outputName, expectedLine + Environment.NewLine);
					File.AppendAllText(outputName, actualLine + Environment.NewLine);
					File.AppendAllText(outputName, new string(' ', firstDiff) + "^");

					throw new InvalidDataException(
						string.Format("Request {0} line {1} differs. Request:\r\n{2}, output written to: {3}",
						              responseNumber, line, request, outputName));
				}
			}
		}

		private static int FindFirstDiff(string expectedLine, string actualLine)
		{
			if (actualLine == null || expectedLine == null)
				return 0;
			for (var i = 0; i < Math.Min(expectedLine.Length, actualLine.Length); i++)
			{
				if (expectedLine[i] != actualLine[i])
					return i;
			}
			return Math.Min(expectedLine.Length, actualLine.Length);
		}

		private static string HandleChunking(byte[] data)
		{
			var memoryStream = new MemoryStream(data);
			var streamReader = new StreamReader(memoryStream);

			var sb = new StringBuilder();
			sb.AppendLine(streamReader.ReadLine()); //status
			string line;
			while ((line = streamReader.ReadLine()) != "")
				sb.AppendLine(line); // header
			sb.AppendLine(); //separator line
			if (sb.ToString().Contains("Transfer-Encoding: chunked") == false)
			{
				sb.Append(streamReader.ReadToEnd());
				return sb.ToString();
			}

			string chunk;
			while (((chunk = ReadChuck(streamReader))) != null)
			{
				sb.Append(chunk);
			}
			return sb.ToString();
		}

		private static string ReadChuck(StreamReader memoryStream)
		{
			var chunkSizeBytes = new List<byte>();
			byte prev = 0;
			byte cur = 0;
			do
			{
				var readByte = memoryStream.Read();
				if (readByte == -1)
					return null;
				prev = cur;
				cur = (byte) readByte;
				chunkSizeBytes.Add(cur);
			} while (!(prev == '\r' && cur == '\n')); // (cur != '\n' && chunkSizeBytes.LastOrDefault() != '\r');

			chunkSizeBytes.RemoveAt(chunkSizeBytes.Count - 1);
			chunkSizeBytes.RemoveAt(chunkSizeBytes.Count - 1);

			if (chunkSizeBytes.Count == 0)
				return null;

			var size = Convert.ToInt32(Encoding.UTF8.GetString(chunkSizeBytes.ToArray()), 16);

			var buffer = new char[size];
			memoryStream.Read(buffer, 0, size); //not doing repeated read because it is all in mem
			memoryStream.Read(); // read next \r
			memoryStream.Read(); // read next \n
			return new string(buffer);
		}
	}
}