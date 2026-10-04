import { auth } from "../config/firebase";

// The signed-in user's current Firebase ID token, kept fresh by the SDK (it refreshes shortly
// before expiry and fires onIdTokenChanged). Needed synchronously for media URLs: an <audio> /
// waveform request cannot carry an Authorization header, so the API also accepts ?access_token=.
let latestToken: string | null = null;

auth.onIdTokenChanged(async (user) => {
  latestToken = user ? await user.getIdToken() : null;
});

export const withAuthToken = (url: string): string =>
  latestToken
    ? `${url}${url.includes("?") ? "&" : "?"}access_token=${encodeURIComponent(latestToken)}`
    : url;
