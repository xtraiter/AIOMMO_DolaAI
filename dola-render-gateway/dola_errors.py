"""Exceptions shared by the UI worker, the account pool and the HTTP layer."""


class LoginRequiredError(RuntimeError):
    """The account is not (or no longer) logged in on Dola. Subclasses RuntimeError for backward compatibility."""


class DolaAskedBackError(Exception):
    """Dola answered the prompt with a question (length limit, missing reference image, options...) instead of
    generating a video. Re-sending the same prompt on another account would get the same question."""


class AccountUnhealthyError(Exception):
    """The pre-flight greeting chat failed: Dola did not create/answer a conversation for this account."""
