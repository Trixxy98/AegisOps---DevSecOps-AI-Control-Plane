import type { ReactNode, SVGProps } from "react";

// 16px line icons, 1.5 stroke on a 24-unit grid. Decorative: hidden from assistive technology.
type IconProps = Omit<SVGProps<SVGSVGElement>, "children"> & { size?: number };

function Icon({ size = 16, children, ...rest }: IconProps & { children: ReactNode }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.5}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      {...rest}
    >
      {children}
    </svg>
  );
}

export function LogoMark({ size = 22 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true" focusable="false" className="logo-mark">
      <path d="M12 2.5 4 5.5v6.1c0 4.7 3.3 8.6 8 9.9 4.7-1.3 8-5.2 8-9.9V5.5l-8-3Z" fill="currentColor" />
      <path
        d="m8.6 12.2 2.4 2.4 4.6-4.8"
        fill="none"
        stroke="var(--accent-contrast)"
        strokeWidth={1.9}
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

export const IconOverview = (props: IconProps) => (
  <Icon {...props}>
    <rect x="3.5" y="3.5" width="7" height="7" rx="1.5" />
    <rect x="13.5" y="3.5" width="7" height="7" rx="1.5" />
    <rect x="3.5" y="13.5" width="7" height="7" rx="1.5" />
    <rect x="13.5" y="13.5" width="7" height="7" rx="1.5" />
  </Icon>
);

export const IconFolder = (props: IconProps) => (
  <Icon {...props}>
    <path d="M3.5 7.5v10a2 2 0 0 0 2 2h13a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-6.2L10.5 5h-5a2 2 0 0 0-2 2.5Z" />
  </Icon>
);

export const IconRocket = (props: IconProps) => (
  <Icon {...props}>
    <path d="M12 15.5 8.5 12c1.6-4.6 5-7.7 11-8.5-.8 6-3.9 9.4-8.5 11Z" />
    <path d="M8.5 12H5l2.5-3.5h4" />
    <path d="M12 15.5V19l3.5-2.5v-4" />
    <path d="M6.5 15.5c-1.5.8-2 2.6-2 4 1.4 0 3.2-.5 4-2" />
  </Icon>
);

export const IconApprovals = (props: IconProps) => (
  <Icon {...props}>
    <circle cx="12" cy="12" r="8.5" />
    <path d="m8.5 12.2 2.4 2.3 4.6-4.8" />
  </Icon>
);

export const IconFindings = (props: IconProps) => (
  <Icon {...props}>
    <path d="M10.3 4.2 2.9 17.1A2 2 0 0 0 4.6 20h14.8a2 2 0 0 0 1.7-2.9L13.7 4.2a2 2 0 0 0-3.4 0Z" />
    <path d="M12 9.5v4" />
    <path d="M12 16.8h.01" />
  </Icon>
);

export const IconPolicies = (props: IconProps) => (
  <Icon {...props}>
    <path d="M12 3.5 5 6.2v5.4c0 4.1 2.9 7.6 7 8.9 4.1-1.3 7-4.8 7-8.9V6.2l-7-2.7Z" />
  </Icon>
);

export const IconSparkles = (props: IconProps) => (
  <Icon {...props}>
    <path d="M10 4.5 11.4 9a2 2 0 0 0 1.3 1.3L17 11.7l-4.3 1.4a2 2 0 0 0-1.3 1.3L10 18.9l-1.4-4.5a2 2 0 0 0-1.3-1.3L3 11.7l4.3-1.4A2 2 0 0 0 8.6 9L10 4.5Z" />
    <path d="M18 3v4M16 5h4" />
  </Icon>
);

export const IconChat = (props: IconProps) => (
  <Icon {...props}>
    <path d="M20.5 12a7.5 7.5 0 0 1-11 6.6L4 20l1.4-4.6A7.5 7.5 0 1 1 20.5 12Z" />
  </Icon>
);

export const IconAudit = (props: IconProps) => (
  <Icon {...props}>
    <path d="M14 3.5H7a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-10l-5-5Z" />
    <path d="M14 3.5v5h5" />
    <path d="M8.5 13h7M8.5 16.5h5" />
  </Icon>
);

export const IconSettings = (props: IconProps) => (
  <Icon {...props}>
    <path d="M4 7h9M17 7h3M4 17h3M11 17h9" />
    <circle cx="15" cy="7" r="2" />
    <circle cx="9" cy="17" r="2" />
  </Icon>
);

export const IconSun = (props: IconProps) => (
  <Icon {...props}>
    <circle cx="12" cy="12" r="3.75" />
    <path d="M12 2.5v2M12 19.5v2M4.6 4.6 6 6M18 18l1.4 1.4M2.5 12h2M19.5 12h2M4.6 19.4 6 18M18 6l1.4-1.4" />
  </Icon>
);

export const IconMoon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5Z" />
  </Icon>
);

export const IconMonitor = (props: IconProps) => (
  <Icon {...props}>
    <rect x="3" y="4.5" width="18" height="12" rx="2" />
    <path d="M8.5 20h7M12 16.5V20" />
  </Icon>
);

export const IconSignOut = (props: IconProps) => (
  <Icon {...props}>
    <path d="M9.5 20.5h-4a2 2 0 0 1-2-2v-13a2 2 0 0 1 2-2h4" />
    <path d="m15.5 16.5 4.5-4.5-4.5-4.5M20 12H9.5" />
  </Icon>
);

export const IconInbox = (props: IconProps) => (
  <Icon {...props}>
    <path d="M3.5 13.5h5l1.5 2.5h4l1.5-2.5h5" />
    <path d="M6 5.5h12l2.5 8v5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-5l2.5-8Z" />
  </Icon>
);

export const IconAlert = (props: IconProps) => (
  <Icon {...props}>
    <circle cx="12" cy="12" r="8.5" />
    <path d="M12 8v4.5M12 15.8h.01" />
  </Icon>
);

export const IconInfo = (props: IconProps) => (
  <Icon {...props}>
    <circle cx="12" cy="12" r="8.5" />
    <path d="M12 11v5M12 8.2h.01" />
  </Icon>
);

export const IconCheck = (props: IconProps) => (
  <Icon {...props}>
    <path d="m5 12.5 4.5 4.5L19 7.5" />
  </Icon>
);

export const IconX = (props: IconProps) => (
  <Icon {...props}>
    <path d="M6.5 6.5l11 11M17.5 6.5l-11 11" />
  </Icon>
);

export const IconChevronLeft = (props: IconProps) => (
  <Icon {...props}>
    <path d="m14.5 6-6 6 6 6" />
  </Icon>
);

export const IconChevronRight = (props: IconProps) => (
  <Icon {...props}>
    <path d="m9.5 6 6 6-6 6" />
  </Icon>
);

export const IconCopy = (props: IconProps) => (
  <Icon {...props}>
    <rect x="8.5" y="8.5" width="12" height="12" rx="2" />
    <path d="M15.5 8.5v-3a2 2 0 0 0-2-2h-8a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h3" />
  </Icon>
);

export const IconPlus = (props: IconProps) => (
  <Icon {...props}>
    <path d="M12 5v14M5 12h14" />
  </Icon>
);
