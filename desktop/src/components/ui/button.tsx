import { Button as ButtonPrimitive } from "@base-ui/react/button";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/utils";

/* The shadcn Base UI button (style `base-nova`), mapped onto the app tokens:
   `rounded-sm` and the colour utilities already resolve to the Hallmark
   tokens through `tokens.css` and the `@theme inline` block. `styles.css`
   keeps its global `button` rules away from `[data-slot="button"]`, so the
   variants below own the look. Type, cursor and the focus ring stay global —
   `button { font: inherit }` is unlayered, so any font utility here would be
   dead weight and the button reads at its container's size, as the rest of
   the app's controls do. */
const buttonVariants = cva(
  "group/button inline-flex shrink-0 items-center justify-center rounded-sm border border-transparent bg-clip-padding whitespace-nowrap transition-colors select-none active:not-aria-[haspopup]:translate-y-px disabled:pointer-events-none disabled:opacity-45 [&_svg]:pointer-events-none [&_svg]:shrink-0",
  {
    variants: {
      variant: {
        default: "bg-primary text-primary-foreground hover:bg-primary/90",
        outline: "border-border bg-card text-foreground hover:bg-menu-hover",
        secondary: "bg-secondary text-foreground hover:bg-menu-hover",
        ghost:
          "text-muted-foreground hover:bg-menu-hover hover:text-foreground",
        destructive:
          "bg-destructive/10 text-destructive hover:bg-destructive/20",
        link: "text-primary underline-offset-4 hover:underline",
      },
      /* Sizes are in px, not the rem-based scale: `html` is 14px here, so
         `min-h-9` would render 31.5px next to the app's 36px controls. These
         match the global button, `.small-button` and `.icon-button`. */
      size: {
        default: "min-h-[36px] gap-[8px] px-[12px] py-[8px]",
        sm: "min-h-[30px] gap-[6px] px-[9px] py-[5px]",
        icon: "size-[36px]",
        "icon-sm": "size-[30px]",
      },
    },
    defaultVariants: {
      variant: "default",
      size: "default",
    },
  },
);

function Button({
  className,
  variant = "default",
  size = "default",
  ...props
}: ButtonPrimitive.Props & VariantProps<typeof buttonVariants>) {
  return (
    <ButtonPrimitive
      data-slot="button"
      className={cn(buttonVariants({ variant, size, className }))}
      {...props}
    />
  );
}

export { Button, buttonVariants };
