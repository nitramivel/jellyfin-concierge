using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// Captures the outgoing request and returns a canned response.
    /// </summary>
    /// <remarks>
    /// Every provider test in this assembly runs against this or
    /// <see cref="SequenceHandler"/>. Hard rule 5: no live LLM or embedding call is
    /// ever made from a test.
    /// </remarks>
    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public StubHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        public HttpRequestMessage? Request { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody),
            };
        }
    }

    /// <param name="Status">The status to return.</param>
    /// <param name="Body">The response body.</param>
    /// <param name="RetryAfterSeconds">A Retry-After header to set, when the case needs one.</param>
    internal sealed record Reply(HttpStatusCode Status, string Body = "{}", int? RetryAfterSeconds = null);

    /// <summary>
    /// Plays a queued sequence of replies and counts the sends, for the retry paths.
    /// The last reply repeats once the queue is drained.
    /// </summary>
    /// <remarks>
    /// A fresh <see cref="HttpResponseMessage"/> is built per send rather than
    /// replaying one instance: the provider disposes each response it reads, so a
    /// repeated instance would come back disposed on the second attempt.
    /// </remarks>
    internal sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Reply> _replies;
        private Reply _last = null!;

        public SequenceHandler(params Reply[] replies)
        {
            _replies = new Queue<Reply>(replies);
        }

        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            if (_replies.Count > 0)
            {
                _last = _replies.Dequeue();
            }

            var response = new HttpResponseMessage(_last.Status)
            {
                Content = new StringContent(_last.Body),
            };

            if (_last.RetryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return Task.FromResult(response);
        }
    }
}
