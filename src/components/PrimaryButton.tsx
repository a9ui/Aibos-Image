'use client';

import Link from 'next/link';
import React from 'react';
import type { ComponentPropsWithoutRef } from 'react';

export interface PrimaryButtonProps {
  label: string;
  href?: string;
  onClick?: ComponentPropsWithoutRef<'button'>['onClick'];
  type?: ComponentPropsWithoutRef<'button'>['type'];
}

const isInternal = (href?: string) => Boolean(href && href.startsWith('/'));

export function PrimaryButton({ label, href, onClick, type = 'button' }: PrimaryButtonProps) {
  if (href && isInternal(href)) {
    return (
      <Link className="primary-button" href={href}>
        {label}
      </Link>
    );
  }

  if (href) {
    return (
      <a className="primary-button" href={href} target="_blank" rel="noreferrer">
        {label}
      </a>
    );
  }

  return (
    <button className="primary-button" type={type} onClick={onClick}>
      {label}
    </button>
  );
}
