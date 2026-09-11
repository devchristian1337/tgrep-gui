import { Accordion as Primitive } from "@base-ui/react/accordion";
import { ChevronDown } from "lucide-react";
import { cn } from "@/lib/utils";

export function Accordion({ className, ...props }: Primitive.Root.Props) {
  return (
    <Primitive.Root
      className={cn("ui-accordion flex w-full flex-col", className)}
      {...props}
    />
  );
}
export function AccordionItem({ className, ...props }: Primitive.Item.Props) {
  return (
    <Primitive.Item className={cn("ui-accordion-item", className)} {...props} />
  );
}
export function AccordionTrigger({
  className,
  children,
  ...props
}: Primitive.Trigger.Props) {
  return (
    <Primitive.Header className="ui-accordion-header">
      <Primitive.Trigger
        className={cn("ui-accordion-trigger", className)}
        {...props}
      >
        {children}
        <ChevronDown className="accordion-chevron" aria-hidden="true" />
      </Primitive.Trigger>
    </Primitive.Header>
  );
}
export function AccordionContent({
  className,
  children,
  ...props
}: Primitive.Panel.Props) {
  return (
    <Primitive.Panel
      className={cn("ui-accordion-content", className)}
      {...props}
    >
      <div className="ui-accordion-inner">{children}</div>
    </Primitive.Panel>
  );
}
