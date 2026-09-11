import {
  BellIcon,
  CreditCardIcon,
  ShieldIcon,
  UserIcon,
  type LucideIcon,
} from "lucide-react";
import {
  Accordion,
  AccordionContent,
  AccordionItem,
  AccordionTrigger,
} from "@/components/ui/accordion";

export type SidebarSection = {
  icon: LucideIcon;
  label: string;
  value: string;
  onSelect?: () => void;
  links: {
    label: string;
    value: string;
    onSelect?: () => void;
    disabled?: boolean;
  }[];
};
const sections: SidebarSection[] = [
  {
    icon: UserIcon,
    label: "Account",
    links: ["Profile", "Display Name", "Email Address", "Language & Region"],
    value: "account",
  },
  {
    icon: ShieldIcon,
    label: "Security",
    links: ["Password", "Two-Factor Auth", "Active Sessions", "Login History"],
    value: "security",
  },
  {
    icon: BellIcon,
    label: "Notifications",
    links: [
      "Email Alerts",
      "Push Notifications",
      "Digest Frequency",
      "Do Not Disturb",
    ],
    value: "notifications",
  },
  {
    icon: CreditCardIcon,
    label: "Billing",
    links: [
      "Subscription Plan",
      "Payment Methods",
      "Invoices",
      "Usage & Limits",
    ],
    value: "billing",
  },
].map((section) => ({
  ...section,
  links: section.links.map((label) => ({ label, value: label })),
}));

export function Pattern({
  items = sections,
  active,
  heading = "Settings",
  defaultValue = ["account"],
}: {
  items?: SidebarSection[];
  active?: string;
  heading?: string;
  defaultValue?: string[];
}) {
  return (
    <div className="settings-sidebar-accordion w-full">
      {heading && <p className="eyebrow">{heading}</p>}
      <Accordion defaultValue={defaultValue} multiple>
        {items.map(({ icon: Icon, label, value, links, onSelect }) => (
          <AccordionItem key={value} value={value}>
            <AccordionTrigger onClick={onSelect}>
              <span className="flex items-center gap-2.5">
                <Icon aria-hidden="true" />
                <span>{label}</span>
              </span>
            </AccordionTrigger>
            <AccordionContent>
              <ul className="accordion-links">
                {links.map((link) => (
                  <li key={link.value}>
                    {link.onSelect ? (
                      <button
                        type="button"
                        disabled={link.disabled}
                        aria-current={
                          active === link.value ? "page" : undefined
                        }
                        onClick={link.onSelect}
                      >
                        {link.label}
                      </button>
                    ) : (
                      <a
                        href={`#${link.value.toLowerCase().replaceAll(" ", "-")}`}
                      >
                        {link.label}
                      </a>
                    )}
                  </li>
                ))}
              </ul>
            </AccordionContent>
          </AccordionItem>
        ))}
      </Accordion>
    </div>
  );
}
export default Pattern;
