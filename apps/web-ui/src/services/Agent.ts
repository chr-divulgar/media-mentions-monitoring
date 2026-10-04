import axios from "axios";
import { auth } from "../config/firebase";

const api = axios.create({
  baseURL: import.meta.env.VITE_API_LOCAL,
});

// Every API call carries the signed-in user's Firebase ID token (the API verifies it and applies
// roles). getIdToken() returns the cached token and transparently refreshes it when it has expired.
api.interceptors.request.use(async (config) => {
  const token = await auth.currentUser?.getIdToken();
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

export default api;
