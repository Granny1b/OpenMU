using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MuSite.Pages;

/// <summary>
/// The page every error status re-executes into.
///
/// It replaces a single "not found" page that Program.cs pointed EVERY status at. A rate-limited
/// registration (429), an expired anti-forgery token (400) and a forbidden request (403) all
/// rendered as "there is no page at that address" - which is not merely unhelpful, it is wrong, and
/// it cost a real user a registration they could not explain the failure of.
/// </summary>
public sealed class StatusModel : PageModel
{
    /// <summary>The status code being explained.</summary>
    public int Code { get; private set; }

    /// <summary>The heading.</summary>
    public string Heading { get; private set; } = "Something went wrong";

    /// <summary>What happened, in the visitor's terms.</summary>
    public string Explanation { get; private set; } = "That request could not be completed.";

    /// <summary>What to do about it, when there is something.</summary>
    public string? NextStep { get; private set; }

    public void OnGet(int? code)
    {
        // The query value is passed by UseStatusCodePagesWithReExecute's format string. Response
        // .StatusCode is the fallback: it holds the original code during a re-execute, so the page
        // stays correct even if the format string is ever dropped.
        this.Code = code ?? this.Response.StatusCode;

        (this.Heading, this.Explanation, this.NextStep) = this.Code switch
        {
            404 => ("Not found",
                    "There is no page, character or guild at that address.",
                    "Character and guild names are looked up as they are spelled in the game, though capitalisation does not matter."),

            429 => ("Too many attempts",
                    "You have made too many requests in a short time, and this one was refused to keep the server usable for everyone.",
                    "Wait a few minutes and try again. Failed attempts count too, so a mistyped password uses one up."),

            400 => ("That form could not be accepted",
                    "The page you submitted from was out of date, so the request was rejected before anything was changed.",
                    "Go back, reload the page, and fill it in again."),

            403 => ("Not allowed",
                    "Your account cannot open that page.",
                    null),

            503 => ("Temporarily unavailable",
                    "The site cannot reach the game database at the moment.",
                    "This usually clears by itself. If it does not, the server owner will see it in the logs."),

            _ => ("Something went wrong",
                  "That request could not be completed. Nothing you did caused it and nothing was lost.",
                  null),
        };
    }
}
