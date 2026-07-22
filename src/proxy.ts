import { type NextRequest, NextResponse } from 'next/server';

import { guardLocalApiRequest } from '@/lib/localApiGuard';

export function proxy(request: NextRequest) {
  return guardLocalApiRequest(request) ?? NextResponse.next();
}

export const config = {
  matcher: ['/api/:path*'],
};
