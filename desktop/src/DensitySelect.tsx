import { useId, useState } from "react";
import { Check, ChevronDown } from "lucide-react";

const options = ["comfortable", "compact"];
const label = (value: string) => value[0].toUpperCase() + value.slice(1);

export default function DensitySelect({
  value,
  onChange,
}: {
  value: string;
  onChange: (value: string) => void;
}) {
  const id = useId();
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const choose = (index: number) => {
    onChange(options[index]);
    setOpen(false);
  };
  return (
    <div
      className="density-select"
      onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget)) setOpen(false);
      }}
    >
      <button
        type="button"
        role="combobox"
        aria-label="Result density"
        aria-expanded={open}
        aria-controls={id}
        aria-haspopup="listbox"
        aria-activedescendant={open ? `${id}-${active}` : undefined}
        onClick={() => {
          setActive(Math.max(0, options.indexOf(value)));
          setOpen(!open);
        }}
        onKeyDown={(event) => {
          if (["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) {
            event.preventDefault();
            setOpen(true);
            setActive(
              event.key === "Home"
                ? 0
                : event.key === "End"
                  ? 1
                  : !open
                    ? Math.max(0, options.indexOf(value))
                    : (active +
                        (event.key === "ArrowDown" ? 1 : -1) +
                        options.length) %
                      options.length,
            );
          } else if ((event.key === "Enter" || event.key === " ") && open) {
            event.preventDefault();
            choose(active);
          } else if (event.key === "Escape" && open) {
            event.preventDefault();
            event.stopPropagation();
            setOpen(false);
          } else if (event.key === "Tab") setOpen(false);
        }}
      >
        {label(value)}
        <ChevronDown size={16} />
      </button>
      {open && (
        <div
          id={id}
          role="listbox"
          aria-label="Result density"
          className="density-options"
        >
          {options.map((option, index) => (
            <div
              key={option}
              id={`${id}-${index}`}
              role="option"
              aria-selected={value === option}
              data-active={active === index}
              onMouseDown={(event) => event.preventDefault()}
              onMouseMove={() => setActive(index)}
              onClick={() => choose(index)}
            >
              {label(option)}
              {value === option && <Check size={16} />}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
