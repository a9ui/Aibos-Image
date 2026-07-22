const LOOPBACK_HOSTNAMES = new Set(['127.0.0.1', 'localhost', '::1']);

type LocalAuthority = {
  hostname: string;
  port: string;
};

function parseLocalAuthority(value: string): LocalAuthority | null {
  const trimmed = value.trim();
  if (!trimmed || trimmed !== value || /[\\/@?#,]/.test(trimmed)) return null;

  try {
    const parsed = new URL(`http://${trimmed}`);
    if (parsed.username || parsed.password || parsed.pathname !== '/' || parsed.search || parsed.hash) {
      return null;
    }

    const hostname = parsed.hostname.toLowerCase().replace(/^\[|\]$/g, '');
    if (!LOOPBACK_HOSTNAMES.has(hostname)) return null;
    return { hostname, port: parsed.port };
  } catch {
    return null;
  }
}

function sameAuthority(left: LocalAuthority, right: LocalAuthority) {
  return left.hostname === right.hostname && left.port === right.port;
}

function forbiddenResponse() {
  return Response.json(
    { error: 'Forbidden local API request.' },
    {
      status: 403,
      headers: {
        'Cache-Control': 'no-store',
      },
    },
  );
}

/**
 * Reject browser and DNS-rebinding requests before a local API reaches any
 * filesystem, shared-state, worker, or OS side effect. Direct loopback clients
 * (the launcher, WPF/CLI probes, and route tests) may omit browser-only Origin
 * and Fetch Metadata headers.
 */
export function guardLocalApiRequest(request: Request): Response | null {
  let requestUrl: URL;
  try {
    requestUrl = new URL(request.url);
  } catch {
    return forbiddenResponse();
  }

  const requestAuthority = parseLocalAuthority(requestUrl.host);
  const hostHeader = request.headers.get('host');
  const hostAuthority = hostHeader === null ? requestAuthority : parseLocalAuthority(hostHeader);

  if (!requestAuthority || !hostAuthority) return forbiddenResponse();

  const fetchSite = request.headers.get('sec-fetch-site');
  if (fetchSite !== null && fetchSite.toLowerCase() !== 'same-origin') {
    return forbiddenResponse();
  }

  const fetchMode = request.headers.get('sec-fetch-mode');
  if (fetchMode !== null && fetchMode.toLowerCase() === 'no-cors') {
    const normalizedSite = fetchSite?.toLowerCase() ?? null;
    const safeSubresourceMethod = request.method === 'GET' || request.method === 'HEAD';
    if (normalizedSite !== 'same-origin' || !safeSubresourceMethod) {
      return forbiddenResponse();
    }
  }

  const origin = request.headers.get('origin');
  if (origin === null) return null;

  try {
    const parsedOrigin = new URL(origin);
    if (parsedOrigin.origin !== origin || parsedOrigin.protocol !== requestUrl.protocol) {
      return forbiddenResponse();
    }
    const originAuthority = parseLocalAuthority(parsedOrigin.host);
    if (!originAuthority || !sameAuthority(originAuthority, hostAuthority)) {
      return forbiddenResponse();
    }
  } catch {
    return forbiddenResponse();
  }

  return null;
}
